using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Persistence.Sessions;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// P10.1 community-session scheduling slice (AT-29; SA-SES-01–SA-SES-04, SA-SES-09,
/// SA-SES-13; PC-08). Uses the disposable loopback intake database gate.
/// </summary>
[Trait("Category", "IntakePostgres")]
[Collection(IntakePostgresCollection.Name)]
public sealed class SessionSchedulingTests
{
    [IntakePostgresFact]
    public async Task StaffHistoryRequiresCurrentAuthorizationAndPagesStoredRevisions()
    {
        var id = Guid.NewGuid();
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var store = new PostgresCommunitySessionStore(db);
        var sessions = new CommunitySessionService(store, new FixtureMaterialEligibility());
        try
        {
            await sessions.ExecuteAsync(CreateDraft(id, "officer-1"), "officer-1", CancellationToken.None);
            await sessions.ExecuteAsync(new ReviewSessionCommand(id, 1, false, "review", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
            await sessions.ExecuteAsync(new ScheduleSessionCommand(id, 2, "publish", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
            var originalStart = (await sessions.GetStaffAsync(id, "officer-1", CancellationToken.None))!.StartUtc;
            await sessions.ExecuteAsync(UpdateDraft(id, 3, "Malaria prevention talk") with { DurationMinutes = 60 }, "officer-2", CancellationToken.None);

            Assert.Null(await new CommunitySessionHistoryReader(store).ReadAsync(id, "officer-1", null, 2, CancellationToken.None));
            var authorization = new HistoryAuthorizer(id, "officer-1");
            var reader = new CommunitySessionHistoryReader(store, authorization);
            Assert.Null(await reader.ReadAsync(id, "another-officer", null, 2, CancellationToken.None));
            Assert.Null(await reader.ReadAsync(Guid.NewGuid(), "officer-1", null, 2, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(id, "officer-1", 0, 2, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(id, "officer-1", null, 101, CancellationToken.None));
            var first = Assert.IsType<SessionHistoryPage>(await reader.ReadAsync(id, "officer-1", null, 2, CancellationToken.None));
            Assert.Equal(new long[] { 4, 3 }, first.Items.Select(item => item.Version));
            Assert.Equal("Rescheduled", first.Items[0].Action);
            Assert.Equal(originalStart, first.Items[0].PreviousStartUtc);
            Assert.Equal(3, first.BeforeVersion);
            var second = Assert.IsType<SessionHistoryPage>(await reader.ReadAsync(id, "officer-1", first.BeforeVersion, 2, CancellationToken.None));
            Assert.Equal(new long[] { 2, 1 }, second.Items.Select(item => item.Version));
            Assert.Null(second.BeforeVersion);

            // Simulate a pre-metadata snapshot in this disposable database.
            await db.Set<CommunitySessionRevisionEntity>().Where(row => row.SessionId == id && row.Version == 1)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.SnapshotJson, "{\"Status\":\"Draft\"}"));
            var legacy = Assert.IsType<SessionHistoryPage>(await reader.ReadAsync(id, "officer-1", 2, 1, CancellationToken.None));
            Assert.Null(Assert.Single(legacy.Items).Action);
            Assert.Null(legacy.Items[0].PreviousStartUtc);
            Assert.Equal("Draft", legacy.Items[0].Status);

            authorization.RevokeAfterNextCheck = true;
            Assert.Null(await reader.ReadAsync(id, "officer-1", null, 2, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(id);
        }
    }

    [IntakePostgresFact]
    public async Task PublicDiscoveryAndAuthenticatedSubscriptionFollowRevisionAndCancellation()
    {
        var id = Guid.NewGuid();
        const string account = "actor-1";
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var sessionStore = new PostgresCommunitySessionStore(db);
        var sessions = new CommunitySessionService(sessionStore, new FixtureMaterialEligibility());
        var subscriptions = new EventSubscriptionService(new PostgresEventSubscriptionStore(db), sessions, TimeProvider.System);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-Test-CSRF");
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
        })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ICommunitySessionService>(sessions);
        builder.Services.AddSingleton<IEventSubscriptionService>(subscriptions);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPublicSessionEndpoints();
        app.MapEventSubscriptionEndpoints();
        app.MapGet("/fixture/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
            antiforgery.GetAndStoreTokens(context).RequestToken!);
        await app.StartAsync();
        using var client = app.GetTestClient();
        try
        {
            await CreateActiveActorAsync(db, account);
            await sessions.ExecuteAsync(CreateDraft(id, "officer-1"), "officer-1", CancellationToken.None);
            await sessions.ExecuteAsync(new ReviewSessionCommand(id, 1, false, "fixture review", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
            await sessions.ExecuteAsync(new ScheduleSessionCommand(id, 2, "fixture publish", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);

            using var publicResponse = await client.GetAsync($"/public/v1/sessions/{id:D}");
            Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
            var publicSession = Assert.IsType<PublicSessionView>(await publicResponse.Content.ReadFromJsonAsync<PublicSessionView>());
            Assert.Equal(3, publicSession.Version);
            var firstCalendar = await client.GetStringAsync($"/public/v1/sessions/{id:D}/calendar");
            Assert.Contains($"UID:swiftaid-community-session-{id:D}", firstCalendar, StringComparison.Ordinal);
            Assert.Contains("SEQUENCE:0", firstCalendar, StringComparison.Ordinal);
            Assert.DoesNotContain(account, firstCalendar, StringComparison.Ordinal);
            var invitation = await client.GetStringAsync($"/public/v1/sessions/{id:D}/invitation?version=3");
            Assert.Contains(publicSession.Title, invitation, StringComparison.Ordinal);
            using var anonymous = await client.GetAsync($"/api/v1/sessions/{id:D}/subscription");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthenticationHandler.BearerToken);
            using var tokenResponse = await client.GetAsync("/fixture/antiforgery");
            tokenResponse.EnsureSuccessStatusCode();
            var token = await tokenResponse.Content.ReadAsStringAsync();
            var cookie = tokenResponse.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
            client.DefaultRequestHeaders.Add("X-Test-CSRF", token);
            var options = await client.GetFromJsonAsync<EventSubscriptionOptions>($"/api/v1/sessions/{id:D}/subscription/options");
            Assert.NotNull(options);
            using var subscribedResponse = await client.PostAsJsonAsync($"/api/v1/sessions/{id:D}/subscription",
                new { sessionVersion = options.SessionVersion, consentReference = options.ConsentReference, channel = "Email" });
            Assert.Equal(HttpStatusCode.OK, subscribedResponse.StatusCode);
            var subscribed = Assert.IsType<EventSubscription>(await subscribedResponse.Content.ReadFromJsonAsync<EventSubscription>());
            Assert.Equal(account, subscribed.AccountId);
            Assert.Equal(EventSubscriptionStatus.Active, subscribed.Status);
            Assert.True(await subscriptions.CanDispatchReminderAsync(id, account, options.SessionVersion, options.ConsentReference, "Email", CancellationToken.None));
            using var replayResponse = await client.PostAsJsonAsync($"/api/v1/sessions/{id:D}/subscription",
                new { sessionVersion = options.SessionVersion, consentReference = options.ConsentReference, channel = "Email" });
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            Assert.Equal(subscribed.Version, (await replayResponse.Content.ReadFromJsonAsync<EventSubscription>())!.Version);

            await sessions.ExecuteAsync(UpdateDraft(id, 3, "Malaria prevention talk") with { DurationMinutes = 60 }, "officer-2", CancellationToken.None);
            Assert.False(await subscriptions.CanDispatchReminderAsync(id, account, options.SessionVersion, options.ConsentReference, "Email", CancellationToken.None));
            var stale = await client.GetFromJsonAsync<EventSubscription>($"/api/v1/sessions/{id:D}/subscription");
            Assert.NotNull(stale);
            Assert.Equal(EventSubscriptionStatus.Unsubscribed, stale.Status);
            Assert.True(stale.HasInactiveStoredConsent);
            var revisedCalendar = await client.GetStringAsync($"/public/v1/sessions/{id:D}/calendar");
            Assert.Contains($"UID:swiftaid-community-session-{id:D}", revisedCalendar, StringComparison.Ordinal);
            Assert.Contains("SEQUENCE:1", revisedCalendar, StringComparison.Ordinal);
            var revisedOptions = await client.GetFromJsonAsync<EventSubscriptionOptions>($"/api/v1/sessions/{id:D}/subscription/options");
            Assert.NotNull(revisedOptions);
            using var renewedResponse = await client.PostAsJsonAsync($"/api/v1/sessions/{id:D}/subscription",
                new { sessionVersion = revisedOptions.SessionVersion, consentReference = revisedOptions.ConsentReference, channel = "Email" });
            Assert.Equal(HttpStatusCode.OK, renewedResponse.StatusCode);
            var renewed = Assert.IsType<EventSubscription>(await renewedResponse.Content.ReadFromJsonAsync<EventSubscription>());
            Assert.True(renewed.Version > subscribed.Version);
            Assert.True(await subscriptions.CanDispatchReminderAsync(id, account, revisedOptions.SessionVersion, revisedOptions.ConsentReference, "Email", CancellationToken.None));

            await sessions.ExecuteAsync(new CancelSessionCommand(id, 4, "fixture cancellation", "cancel", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
            using var cancelledResponse = await client.GetAsync($"/public/v1/sessions/{id:D}/calendar");
            Assert.Equal(HttpStatusCode.OK, cancelledResponse.StatusCode);
            Assert.Contains("STATUS:CANCELLED", await cancelledResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain("fixture cancellation", await cancelledResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.False(await subscriptions.CanDispatchReminderAsync(id, account, revisedOptions.SessionVersion, revisedOptions.ConsentReference, "Email", CancellationToken.None));
            using var unsubscribedResponse = await client.PostAsJsonAsync($"/api/v1/sessions/{id:D}/subscription/unsubscribe", new { expectedVersion = renewed.Version });
            Assert.Equal(HttpStatusCode.OK, unsubscribedResponse.StatusCode);
            Assert.Equal(EventSubscriptionStatus.Unsubscribed, (await unsubscribedResponse.Content.ReadFromJsonAsync<EventSubscription>())!.Status);
        }
        finally
        {
            await CleanupAsync(id);
            await CleanupActorAsync(account);
        }
    }

    [IntakePostgresFact]
    public async Task ReschedulingDoesNotPersistPastReminderOccurrences()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var start = now.AddHours(2);
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var clock = new TestClock(now.AddDays(-2));
        var service = new CommunitySessionService(new PostgresCommunitySessionStore(db), new FixtureMaterialEligibility(), clock);
        try
        {
            await service.ExecuteAsync(CreateDraft(id, "officer-1") with { StartUtc = start.AddHours(1), RecordedAtUtc = now.AddDays(-2) }, "officer-1", CancellationToken.None);
            await service.ExecuteAsync(new ReviewSessionCommand(id, 1, false, "review", now.AddDays(-2)), "officer-2", CancellationToken.None);
            await service.ExecuteAsync(new ScheduleSessionCommand(id, 2, "schedule", now.AddDays(-2)), "officer-2", CancellationToken.None);
            clock.UtcNow = now;
            await service.ExecuteAsync(UpdateDraft(id, 3, "Malaria prevention talk") with
            {
                StartUtc = start,
                DurationMinutes = 60,
                RecordedAtUtc = now.AddDays(-10)
            }, "officer-2", CancellationToken.None);

            var pending = await db.SessionReminderOccurrences.AsNoTracking()
                .Where(row => row.SessionId == id && row.Status == "Pending").ToListAsync();
            var occurrence = Assert.Single(pending);
            Assert.Equal(1, occurrence.LeadHours);
            Assert.True(occurrence.RemindAtUtc > now);
            Assert.Equal(4, occurrence.Version);
            var revision = await db.Set<CommunitySessionRevisionEntity>().AsNoTracking()
                .SingleAsync(row => row.SessionId == id && row.Version == 4);
            Assert.Equal(now.ToUnixTimeMilliseconds(), revision.RecordedAtUtc.ToUnixTimeMilliseconds());
        }
        finally
        {
            await CleanupAsync(id);
        }
    }

    [IntakePostgresFact]
    public async Task DestinationEndpointRejectsPersistedRevisionAndCancellation()
    {
        var sessionId = Guid.NewGuid();
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = new CommunitySessionService(new PostgresCommunitySessionStore(db), new FixtureMaterialEligibility());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ICommunitySessionService>(service);
        await using var app = builder.Build();
        app.UseAuthorization();
        app.MapPublicSessionEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        try
        {
            await service.ExecuteAsync(CreateDraft(sessionId, "officer-1"), "officer-1", CancellationToken.None);
            await service.ExecuteAsync(new ReviewSessionCommand(sessionId, 1, false, "review", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
            await service.ExecuteAsync(new ScheduleSessionCommand(sessionId, 2, "publish", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
            var path = $"/public/v1/sessions/{sessionId:D}/destination?version=";
            using var first = await client.GetAsync(path + "3");
            Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
            Assert.Equal("https://chat.example/join/abc", first.Headers.Location?.AbsoluteUri);
            Assert.True(first.Headers.CacheControl?.NoStore);
            var invitationPath = $"/public/v1/sessions/{sessionId:D}/invitation?version=";
            using var firstInvitation = await client.GetAsync(invitationPath + "3");
            Assert.Equal(HttpStatusCode.OK, firstInvitation.StatusCode);
            Assert.Contains(PublicSessionInvitationFeatures.Version, firstInvitation.Headers.GetValues(PublicSessionInvitationFeatures.HeaderName));

            await service.ExecuteAsync(UpdateDraft(sessionId, 3, "Malaria prevention talk") with { DurationMinutes = 60 }, "officer-2", CancellationToken.None);
            using var stale = await client.GetAsync(path + "3");
            Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
            using var staleInvitation = await client.GetAsync(invitationPath + "3");
            Assert.Equal(HttpStatusCode.NotFound, staleInvitation.StatusCode);
            using var revisedInvitation = await client.GetAsync(invitationPath + "4");
            Assert.Equal(HttpStatusCode.OK, revisedInvitation.StatusCode);
            using var revised = await client.GetAsync(path + "4");
            Assert.Equal(HttpStatusCode.Redirect, revised.StatusCode);

            await service.ExecuteAsync(new CancelSessionCommand(sessionId, 4, "Private reason", "cancel", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
            using var cancelled = await client.GetAsync(path + "4");
            Assert.Equal(HttpStatusCode.NotFound, cancelled.StatusCode);
            Assert.True(cancelled.Headers.CacheControl?.NoStore);
            Assert.DoesNotContain("Private reason", await cancelled.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var cancelledCurrent = await client.GetAsync(path + "5");
            Assert.Equal(HttpStatusCode.NotFound, cancelledCurrent.StatusCode);
        }
        finally
        {
            await CleanupAsync(sessionId);
        }
    }

    [IntakePostgresFact]
    public async Task CompletedPublishedSessionsRemainInPublicQueriesButInternalSessionsStayPrivate()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var marker = $"completed-{Guid.NewGuid():N}";
        var start = DateTimeOffset.UtcNow.AddDays(5);
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var service = new CommunitySessionService(new PostgresCommunitySessionStore(db), new FixtureMaterialEligibility(), clock);
        try
        {
            for (var index = 0; index < ids.Length; index++)
            {
                clock.UtcNow = DateTimeOffset.UtcNow;
                var draft = CreateDraft(ids[index], "officer-1") with
                {
                    Title = marker,
                    StartUtc = start,
                    Visibility = index == 0 ? SessionVisibility.Public : SessionVisibility.Internal
                };
                await service.ExecuteAsync(draft, "officer-1", CancellationToken.None);
                await service.ExecuteAsync(new ReviewSessionCommand(ids[index], 1, false, "review", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
                await service.ExecuteAsync(new ScheduleSessionCommand(ids[index], 2, "publish", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
                clock.UtcNow = start.AddHours(1);
                await service.ExecuteAsync(new CompleteSessionCommand(ids[index], 3, "complete", start.AddHours(1)), "officer-2", CancellationToken.None);
            }

            var page = await service.QueryPublicAsync(new PublicSessionQuery(Search: marker), CancellationToken.None);
            var result = Assert.Single(page.Items);
            Assert.Equal(ids[0], result.SessionId);
            Assert.Equal("Completed", result.Status);
            Assert.Single((await service.QueryPublicAsync(new PublicSessionQuery(Search: marker, Status: "completed"), CancellationToken.None)).Items);
            Assert.Empty((await service.QueryPublicAsync(new PublicSessionQuery(Search: marker, Status: "Scheduled"), CancellationToken.None)).Items);
            Assert.Null(result.ChannelLink);
            Assert.NotNull((await service.GetStaffAsync(ids[0], "officer-2", CancellationToken.None))!.ChannelLink);
            Assert.DoesNotContain("https://chat.example/join/abc", (await service.GetIcsAsync(ids[0], start.AddHours(2), CancellationToken.None))!, StringComparison.Ordinal);
            Assert.Null(await service.GetInvitationTextAsync(ids[0], CancellationToken.None));
            var listed = await service.ListPublicAsync(100, CancellationToken.None);
            Assert.Contains(listed, item => item.SessionId == ids[0] && item.Status == "Completed");
            Assert.DoesNotContain(listed, item => item.SessionId == ids[1]);
        }
        finally
        {
            await CleanupAsync(ids);
        }
    }

    [IntakePostgresFact]
    public async Task Public_query_filters_local_dates_before_paging_across_midnight()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var day = DateTimeOffset.UtcNow.AddDays(14).UtcDateTime.Date;
        var utcMidnight = new DateTimeOffset(day, TimeSpan.Zero);
        var marker = $"local-date-{Guid.NewGuid():N}";
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = new CommunitySessionService(new PostgresCommunitySessionStore(db), new FixtureMaterialEligibility());
        try
        {
            for (var index = 0; index < ids.Length; index++)
            {
                var draft = CreateDraft(ids[index], "officer-1") with
                {
                    Title = marker,
                    StartUtc = utcMidnight.AddHours(index == 0 ? 1 : 8),
                    TimeZoneId = "America/New_York"
                };
                await service.ExecuteAsync(draft, "officer-1", CancellationToken.None);
                await service.ExecuteAsync(new ReviewSessionCommand(ids[index], 1, false, "review", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
                await service.ExecuteAsync(new ScheduleSessionCommand(ids[index], 2, "publish", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
            }
            var localDay = DateOnly.FromDateTime(day);
            var page = await service.QueryPublicAsync(new PublicSessionQuery(
                Search: marker, LocalDateFrom: localDay, LocalDateTo: localDay, Limit: 1), CancellationToken.None);
            Assert.Equal(ids[1], Assert.Single(page.Items).SessionId);
            Assert.Null(page.Next);
        }
        finally
        {
            await CleanupAsync(ids);
        }
    }

    [IntakePostgresFact]
    public async Task Public_query_filters_before_keyset_pagination_and_hides_unpublished_cancellation()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var marker = $"session-{Guid.NewGuid():N}";
        var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeSeconds());
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = new CommunitySessionService(new PostgresCommunitySessionStore(db), new FixtureMaterialEligibility());
        try
        {
            for (var index = 0; index < ids.Length; index++)
            {
                var draft = CreateDraft(ids[index], "officer-1") with
                {
                    Title = marker,
                    StartUtc = start,
                    Visibility = index == 2 ? SessionVisibility.Internal : SessionVisibility.Public
                };
                await service.ExecuteAsync(draft, "officer-1", CancellationToken.None);
                await service.ExecuteAsync(new ReviewSessionCommand(ids[index], 1, false, "review", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
                if (index == 3)
                    await service.ExecuteAsync(new CancelSessionCommand(ids[index], 2, "cancelled", "cancel", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
                else
                    await service.ExecuteAsync(new ScheduleSessionCommand(ids[index], 2, "publish", DateTimeOffset.UtcNow), "officer-2", CancellationToken.None);
            }

            var query = new PublicSessionQuery(Search: marker, CountryCode: "zw", Category: "malaria", Language: "EN", Limit: 1);
            var first = await service.QueryPublicAsync(query, CancellationToken.None);
            Assert.Single(first.Items);
            Assert.NotNull(first.Next);
            var second = await service.QueryPublicAsync(query with
            {
                AfterStartUtc = first.Next.StartUtc,
                AfterSessionId = first.Next.SessionId
            }, CancellationToken.None);
            Assert.Single(second.Items);
            Assert.Null(second.Next);
            Assert.Equal(ids.Take(2).Order().ToArray(), first.Items.Concat(second.Items).Select(item => item.SessionId).Order().ToArray());
            Assert.Empty((await service.QueryPublicAsync(query with { Language = "not-a-language" }, CancellationToken.None)).Items);
            Assert.NotEmpty((await service.QueryPublicAsync(query with { Category = "humanhealth" }, CancellationToken.None)).Items);
            Assert.NotEmpty((await service.QueryPublicAsync(query with { Sector = "humanhealth", Topic = "malaria" }, CancellationToken.None)).Items);
            Assert.Empty((await service.QueryPublicAsync(query with { Sector = "another-sector", Topic = "malaria" }, CancellationToken.None)).Items);
            Assert.Empty((await service.QueryPublicAsync(query with { Sector = "humanhealth", Topic = "Malaria" }, CancellationToken.None)).Items);
            Assert.NotEmpty((await service.QueryPublicAsync(query with { Status = "scheduled" }, CancellationToken.None)).Items);
            Assert.Empty((await service.QueryPublicAsync(query with { Status = "completed" }, CancellationToken.None)).Items);
            await Assert.ThrowsAsync<ArgumentException>(() => service.QueryPublicAsync(query with { Status = "Draft" }, CancellationToken.None));
            Assert.Null(await service.GetPublicAsync(ids[3], CancellationToken.None));
            Assert.DoesNotContain(await service.ListPublicAsync(null, CancellationToken.None), item => item.SessionId == ids[3]);
        }
        finally
        {
            await CleanupAsync(ids);
        }
    }

    [IntakePostgresFact]
    public async Task Public_outputs_hide_an_unsafe_destination_from_legacy_storage()
    {
        var sessionId = Guid.NewGuid();
        const string unsafeLink = "https://user:password@example.test/session";
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var setup = CreateService(db);
        var service = new CommunitySessionService(new PostgresCommunitySessionStore(db), new FixtureMaterialEligibility());
        try
        {
            await PublishAsync(setup, sessionId, SessionVisibility.Public);
            await db.CommunitySessions.Where(item => item.SessionId == sessionId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.ChannelLink, unsafeLink));

            Assert.Equal(unsafeLink, (await service.GetStaffAsync(sessionId, "officer-1", CancellationToken.None))!.ChannelLink);
            Assert.Null((await service.GetPublicAsync(sessionId, CancellationToken.None))!.ChannelLink);
            Assert.Null(Assert.Single((await service.QueryPublicAsync(new PublicSessionQuery(), CancellationToken.None)).Items).ChannelLink);
            Assert.DoesNotContain("password", (await service.GetIcsAsync(sessionId, DateTimeOffset.UtcNow, CancellationToken.None))!, StringComparison.Ordinal);
            Assert.DoesNotContain("password", (await service.GetInvitationTextAsync(sessionId, CancellationToken.None))!, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(sessionId);
        }
    }

    [IntakePostgresFact]
    public async Task Create_persists_draft_and_revision()
    {
        var sessionId = Guid.NewGuid();
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = CreateService(db);
        try
        {
            var result = await service.ExecuteAsync(CreateDraft(sessionId, "officer-1"), "officer-1", CancellationToken.None);

            Assert.Equal(sessionId, result.SessionId);
            Assert.Equal(1, result.Version);
            Assert.Equal("Draft", result.Status);

            var view = await service.GetStaffAsync(sessionId, "officer-1", CancellationToken.None);
            Assert.NotNull(view);
            Assert.Equal("Malaria prevention talk", view.Title);
            Assert.Equal(SessionVisibility.Public, view.Visibility);
            Assert.Equal(0, view.IcsSequence);

            Assert.Equal(1, await service.StoreCountRevisionsAsync(sessionId, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(sessionId);
        }
    }

    [IntakePostgresFact]
    public async Task Schedule_requires_reviewed_state()
    {
        var sessionId = Guid.NewGuid();
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = CreateService(db);
        try
        {
            await service.ExecuteAsync(CreateDraft(sessionId, "officer-1"), "officer-1", CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ExecuteAsync(
                    new ScheduleSessionCommand(sessionId, 1, "publish", DateTimeOffset.UtcNow),
                    "officer-2",
                    CancellationToken.None));

            await service.ExecuteAsync(
                new ReviewSessionCommand(sessionId, 1, false, "reviewed", DateTimeOffset.UtcNow),
                "officer-2",
                CancellationToken.None);
            var scheduled = await service.ExecuteAsync(
                new ScheduleSessionCommand(sessionId, 2, "publish", DateTimeOffset.UtcNow),
                "officer-2",
                CancellationToken.None);

            Assert.Equal(3, scheduled.Version);
            Assert.Equal("Scheduled", scheduled.Status);
        }
        finally
        {
            await CleanupAsync(sessionId);
        }
    }

    [IntakePostgresFact]
    public async Task Create_maps_Harare_zone_to_UTC_and_rejects_unknown_zone()
    {
        var sessionId = Guid.NewGuid();
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = CreateService(db);
        try
        {
            var localStart = new DateTimeOffset(2027, 11, 5, 12, 0, 0, TimeSpan.FromHours(2));
            var command = CreateDraft(sessionId, "officer-1") with
            {
                StartUtc = localStart,
                TimeZoneId = HarareZoneId(),
            };
            await service.ExecuteAsync(command, "officer-1", CancellationToken.None);

            var view = await service.GetStaffAsync(sessionId, "officer-1", CancellationToken.None);
            Assert.NotNull(view);
            Assert.Equal(localStart.ToUniversalTime(), view.StartUtc);

            var badId = Guid.NewGuid();
            try
            {
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    service.ExecuteAsync(CreateDraft(badId, "officer-1") with { TimeZoneId = "Not/AZone" },
                        "officer-1",
                        CancellationToken.None));
            }
            finally
            {
                await CleanupAsync(badId);
            }
        }
        finally
        {
            await CleanupAsync(sessionId);
        }
    }

    [IntakePostgresFact]
    public async Task SelfReview_requires_acknowledgment_but_other_reviewer_passes()
    {
        var selfId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = CreateService(db);
        try
        {
            await service.ExecuteAsync(CreateDraft(selfId, "officer-1"), "officer-1", CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ExecuteAsync(
                    new ReviewSessionCommand(selfId, 1, false, "self review", DateTimeOffset.UtcNow),
                    "officer-1",
                    CancellationToken.None));

            var selfReviewed = await service.ExecuteAsync(
                new ReviewSessionCommand(selfId, 1, true, "self review acknowledged", DateTimeOffset.UtcNow),
                "officer-1",
                CancellationToken.None);
            Assert.Equal(2, selfReviewed.Version);
            var selfView = await service.GetStaffAsync(selfId, "officer-1", CancellationToken.None);
            Assert.NotNull(selfView);
            Assert.True(selfView.SelfReviewAcknowledged);
            await service.ExecuteAsync(UpdateDraft(selfId, 2, "Edited after self review"), "officer-1", CancellationToken.None);
            var historicalReview = await db.Set<CommunitySessionRevisionEntity>().AsNoTracking()
                .SingleAsync(row => row.SessionId == selfId && row.Version == 2);
            using var snapshot = JsonDocument.Parse(historicalReview.SnapshotJson);
            Assert.Equal("officer-1", snapshot.RootElement.GetProperty("CreatedByActorId").GetString());
            Assert.Equal("officer-1", snapshot.RootElement.GetProperty("ReviewedByActorId").GetString());
            Assert.True(snapshot.RootElement.GetProperty("SelfReviewAcknowledged").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, snapshot.RootElement.GetProperty("ReviewedAtUtc").ValueKind);
            var current = await service.GetStaffAsync(selfId, "officer-1", CancellationToken.None);
            Assert.Null(current!.ReviewedByActorId);
            Assert.False(current.SelfReviewAcknowledged);

            await service.ExecuteAsync(CreateDraft(otherId, "officer-1"), "officer-1", CancellationToken.None);
            var otherReviewed = await service.ExecuteAsync(
                new ReviewSessionCommand(otherId, 1, false, "peer review", DateTimeOffset.UtcNow),
                "officer-2",
                CancellationToken.None);
            Assert.Equal(2, otherReviewed.Version);
            var otherRevision = await db.Set<CommunitySessionRevisionEntity>().AsNoTracking()
                .SingleAsync(row => row.SessionId == otherId && row.Version == 2);
            using var otherSnapshot = JsonDocument.Parse(otherRevision.SnapshotJson);
            Assert.Equal("officer-2", otherSnapshot.RootElement.GetProperty("ReviewedByActorId").GetString());
            Assert.False(otherSnapshot.RootElement.GetProperty("SelfReviewAcknowledged").GetBoolean());
        }
        finally
        {
            await CleanupAsync(selfId, otherId);
        }
    }

    [IntakePostgresFact]
    public async Task Editing_reviewed_session_requires_a_new_review_after_reload()
    {
        var sessionId = Guid.NewGuid();
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = CreateService(db);
        try
        {
            await service.ExecuteAsync(CreateDraft(sessionId, "officer-1"), "officer-1", CancellationToken.None);
            await service.ExecuteAsync(
                new ReviewSessionCommand(sessionId, 1, false, "reviewed", DateTimeOffset.UtcNow),
                "officer-2", CancellationToken.None);
            await service.ExecuteAsync(UpdateDraft(sessionId, 2, "Revised session"), "officer-1", CancellationToken.None);

            var edited = await service.GetStaffAsync(sessionId, "officer-1", CancellationToken.None);
            Assert.NotNull(edited);
            Assert.Equal("Draft", edited.Status);
            Assert.Null(edited.ReviewedByActorId);
            Assert.False(edited.SelfReviewAcknowledged);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
                new ScheduleSessionCommand(sessionId, 3, "publish", DateTimeOffset.UtcNow),
                "officer-2", CancellationToken.None));

            await service.ExecuteAsync(
                new ReviewSessionCommand(sessionId, 3, false, "reviewed revision", DateTimeOffset.UtcNow),
                "officer-2", CancellationToken.None);
            var scheduled = await service.ExecuteAsync(
                new ScheduleSessionCommand(sessionId, 4, "publish", DateTimeOffset.UtcNow),
                "officer-2", CancellationToken.None);
            Assert.Equal("Scheduled", scheduled.Status);
            Assert.Equal(5, await service.StoreCountRevisionsAsync(sessionId, CancellationToken.None));
            var history = await db.Set<CommunitySessionRevisionEntity>().AsNoTracking()
                .Where(row => row.SessionId == sessionId).OrderBy(row => row.Version).ToListAsync();
            var actions = history.Select(row =>
            {
                using var snapshot = JsonDocument.Parse(row.SnapshotJson);
                return snapshot.RootElement.GetProperty("Action").GetString();
            }).ToArray();
            Assert.Equal(new[] { "Created", "Reviewed", "Updated", "Reviewed", "Scheduled" }, actions);
        }
        finally
        {
            await CleanupAsync(sessionId);
        }
    }

    [IntakePostgresFact]
    public async Task Cancel_suppresses_pending_reminders_but_retains_revisions()
    {
        var sessionId = Guid.NewGuid();
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = CreateService(db);
        try
        {
            await service.ExecuteAsync(CreateDraft(sessionId, "officer-1"), "officer-1", CancellationToken.None);
            await service.ExecuteAsync(
                new ReviewSessionCommand(sessionId, 1, false, "reviewed", DateTimeOffset.UtcNow),
                "officer-2",
                CancellationToken.None);
            await service.ExecuteAsync(
                new ScheduleSessionCommand(sessionId, 2, "publish", DateTimeOffset.UtcNow),
                "officer-2",
                CancellationToken.None);

            Assert.Equal(2, await service.StoreCountPendingRemindersAsync(sessionId, CancellationToken.None));

            var update = UpdateDraft(sessionId, 3, "Malaria prevention talk") with { DurationMinutes = 60 };
            await service.ExecuteAsync(update, "officer-2", CancellationToken.None);
            Assert.Equal(2, await service.StoreCountPendingRemindersAsync(sessionId, CancellationToken.None));

            var cancelled = await service.ExecuteAsync(
                new CancelSessionCommand(sessionId, 4, "Venue flooded", "cancel", DateTimeOffset.UtcNow),
                "officer-2",
                CancellationToken.None);
            Assert.Equal("Cancelled", cancelled.Status);

            Assert.Equal(0, await service.StoreCountPendingRemindersAsync(sessionId, CancellationToken.None));
            Assert.Equal(2, await service.StoreCountSuppressedRemindersAsync(sessionId, CancellationToken.None));
            Assert.Equal(5, await service.StoreCountRevisionsAsync(sessionId, CancellationToken.None));

            var listed = await service.ListPublicAsync(null, CancellationToken.None);
            Assert.Contains(listed, item => item.SessionId == sessionId && item.Status == "Cancelled");
        }
        finally
        {
            await CleanupAsync(sessionId);
        }
    }

    [IntakePostgresFact]
    public async Task Public_cancelled_session_does_not_expose_staff_cancellation_reason()
    {
        var sessionId = Guid.NewGuid();
        const string reason = "Private coordination note";
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var setup = CreateService(db);
        var service = new CommunitySessionService(new PostgresCommunitySessionStore(db), new FixtureMaterialEligibility());
        try
        {
            await PublishAsync(setup, sessionId, SessionVisibility.Public);
            await setup.ExecuteAsync(new CancelSessionCommand(sessionId, 3, reason, "cancel", DateTimeOffset.UtcNow),
                "officer-2", CancellationToken.None);

            Assert.Equal(reason, (await service.GetStaffAsync(sessionId, "officer-1", CancellationToken.None))!.CancellationReason);
            var publicSession = await service.GetPublicAsync(sessionId, CancellationToken.None);
            Assert.Equal("Cancelled", publicSession!.Status);
            Assert.Null(publicSession.CancellationReason);
            Assert.Null(publicSession.ChannelLink);
            Assert.NotNull((await service.GetStaffAsync(sessionId, "officer-1", CancellationToken.None))!.ChannelLink);
            var calendar = (await service.GetIcsAsync(sessionId, DateTimeOffset.UtcNow, CancellationToken.None))!;
            Assert.Contains("STATUS:CANCELLED", calendar, StringComparison.Ordinal);
            Assert.DoesNotContain(reason, calendar, StringComparison.Ordinal);
            Assert.DoesNotContain(reason, (await service.GetInvitationTextAsync(sessionId, CancellationToken.None))!, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(sessionId);
        }
    }

    [IntakePostgresFact]
    public async Task Schedule_update_advances_ICS_and_invitation_is_manual_copy()
    {
        var sessionId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = CreateService(db);
        try
        {
            await service.ExecuteAsync(CreateDraft(sessionId, "officer-1"), "officer-1", CancellationToken.None);
            await service.ExecuteAsync(
                new ReviewSessionCommand(sessionId, 1, false, "reviewed", DateTimeOffset.UtcNow),
                "officer-2",
                CancellationToken.None);
            await service.ExecuteAsync(
                new ScheduleSessionCommand(sessionId, 2, "publish", DateTimeOffset.UtcNow),
                "officer-2",
                CancellationToken.None);

            var firstIcs = await service.GetIcsAsync(sessionId, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.NotNull(firstIcs);
            Assert.Contains("SEQUENCE:0", firstIcs, StringComparison.Ordinal);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
                UpdateDraft(sessionId, 3, "Unreviewed content") with { DurationMinutes = 60 }, "officer-2", CancellationToken.None));
            Assert.Equal(3, await service.StoreCountRevisionsAsync(sessionId, CancellationToken.None));
            var previousStart = (await service.GetStaffAsync(sessionId, "officer-2", CancellationToken.None))!.StartUtc;
            await service.ExecuteAsync(UpdateDraft(sessionId, 3, "Malaria prevention talk") with { DurationMinutes = 60 }, "officer-2", CancellationToken.None);

            var revision = await db.Set<CommunitySessionRevisionEntity>().AsNoTracking()
                .SingleAsync(row => row.SessionId == sessionId && row.Version == 4);
            using var revisionSnapshot = JsonDocument.Parse(revision.SnapshotJson);
            Assert.Equal("Rescheduled", revisionSnapshot.RootElement.GetProperty("Action").GetString());
            Assert.Equal(previousStart, revisionSnapshot.RootElement.GetProperty("PreviousStartUtc").GetDateTimeOffset());

            var secondIcs = await service.GetIcsAsync(sessionId, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.NotNull(secondIcs);
            Assert.Contains("SEQUENCE:1", secondIcs, StringComparison.Ordinal);

            var invitation = await service.GetInvitationTextAsync(sessionId, CancellationToken.None);
            Assert.NotNull(invitation);
            Assert.Contains("https://chat.example/join/abc", invitation, StringComparison.Ordinal);

            await service.ExecuteAsync(CreateDraft(draftId, "officer-1"), "officer-1", CancellationToken.None);
            Assert.Null(await service.GetIcsAsync(draftId, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.Null(await service.GetInvitationTextAsync(draftId, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(sessionId, draftId);
        }
    }

    [IntakePostgresFact]
    public async Task Public_listing_excludes_internal_and_draft_sessions()
    {
        var publicId = Guid.NewGuid();
        var internalId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = CreateService(db);
        try
        {
            await PublishAsync(service, publicId, SessionVisibility.Public);
            await PublishAsync(service, internalId, SessionVisibility.Internal);
            await service.ExecuteAsync(CreateDraft(draftId, "officer-1"), "officer-1", CancellationToken.None);

            var listed = await service.ListPublicAsync(null, CancellationToken.None);
            Assert.Contains(listed, item => item.SessionId == publicId);
            Assert.DoesNotContain(listed, item => item.SessionId == internalId);
            Assert.DoesNotContain(listed, item => item.SessionId == draftId);

            Assert.NotNull(await service.GetPublicAsync(publicId, CancellationToken.None));
            Assert.Null(await service.GetPublicAsync(internalId, CancellationToken.None));
            Assert.Null(await service.GetPublicAsync(draftId, CancellationToken.None));
            Assert.Null(await service.GetPublicAsync(Guid.NewGuid(), CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(publicId, internalId, draftId);
        }
    }

    [IntakePostgresFact]
    public async Task Stale_expected_version_conflicts()
    {
        var sessionId = Guid.NewGuid();
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var service = CreateService(db);
        try
        {
            await service.ExecuteAsync(CreateDraft(sessionId, "officer-1"), "officer-1", CancellationToken.None);

            var conflict = await Assert.ThrowsAsync<SessionVersionConflictException>(() =>
                service.ExecuteAsync(
                    new ReviewSessionCommand(sessionId, 999, false, "stale", DateTimeOffset.UtcNow),
                    "officer-2",
                    CancellationToken.None));
            Assert.Equal(sessionId, conflict.SessionId);
            Assert.Equal(999, conflict.ExpectedVersion);
            Assert.Equal(1, conflict.ActualVersion);
        }
        finally
        {
            await CleanupAsync(sessionId);
        }
    }

    [IntakePostgresFact]
    public async Task Event_subscription_is_idempotent_and_unsubscribe_suppresses_dispatch()
    {
        var sessionId = Guid.NewGuid();
        const string accountId = "subscription-owner-1";
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var sessionStore = new PostgresCommunitySessionStore(db);
        var sessions = new CommunitySessionService(sessionStore, new FixtureMaterialEligibility());
        var service = new EventSubscriptionService(new PostgresEventSubscriptionStore(db), sessions, TimeProvider.System);
        var setup = CreateService(db);
        try
        {
            await CreateActiveActorAsync(db, accountId);
            await PublishAsync(setup, sessionId, SessionVisibility.Public);
            var options = await service.GetOptionsAsync(sessionId, accountId, CancellationToken.None);
            Assert.NotNull(options);
            Assert.Contains("Email", options.AvailableChannels);

            var subscribed = await service.SubscribeAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "Email", CancellationToken.None);
            var repeated = await service.SubscribeAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "Email", CancellationToken.None);
            Assert.Equal(1, subscribed.Version);
            Assert.Equal(subscribed.Version, repeated.Version);
            Assert.True(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "Email", CancellationToken.None));

            var changedOptions = (await service.GetOptionsAsync(sessionId, accountId, CancellationToken.None))!;
            var changed = await service.SubscribeAsync(sessionId, accountId, changedOptions.SessionVersion,
                changedOptions.ConsentReference, "MobilePush", CancellationToken.None);
            Assert.Equal(2, changed.Version);
            var actor = await db.Actors.SingleAsync(item => item.ActorId == accountId);
            actor.Status = SwiftAid.Infrastructure.Persistence.PersistentActorStatus.Suspended;
            await db.SaveChangesAsync();
            Assert.False(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                changedOptions.ConsentReference, "MobilePush", CancellationToken.None));

            actor.Status = SwiftAid.Infrastructure.Persistence.PersistentActorStatus.Active;
            await db.SaveChangesAsync();
            Assert.True(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                changedOptions.ConsentReference, "MobilePush", CancellationToken.None));

            var unsubscribed = await service.UnsubscribeAsync(sessionId, accountId, changed.Version, CancellationToken.None);
            Assert.Equal(EventSubscriptionStatus.Unsubscribed, unsubscribed.Status);
            Assert.Equal(3, unsubscribed.Version);
            Assert.False(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                changedOptions.ConsentReference, "MobilePush", CancellationToken.None));
            await Assert.ThrowsAsync<SessionSubscriptionConflictException>(() => service.SubscribeAsync(sessionId, accountId,
                options.SessionVersion, options.ConsentReference, "Email", CancellationToken.None));
            var renewedOptions = (await service.GetOptionsAsync(sessionId, accountId, CancellationToken.None))!;
            var renewed = await service.SubscribeAsync(sessionId, accountId, options.SessionVersion,
                renewedOptions.ConsentReference, "MobilePush", CancellationToken.None);
            Assert.Equal(4, renewed.Version);
            Assert.False(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                changedOptions.ConsentReference, "MobilePush", CancellationToken.None, changed.Version));
            Assert.True(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                renewedOptions.ConsentReference, "MobilePush", CancellationToken.None, renewed.Version));
        }
        finally
        {
            await CleanupAsync(sessionId);
            await CleanupActorAsync(accountId);
        }
    }

    [IntakePostgresFact]
    public async Task Subscription_write_rejects_a_session_cancelled_after_options_were_read()
    {
        var sessionId = Guid.NewGuid();
        const string accountId = "subscription-stale-cancellation";
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var store = new PostgresEventSubscriptionStore(db);
        var setup = CreateService(db);
        try
        {
            await CreateActiveActorAsync(db, accountId);
            await PublishAsync(setup, sessionId, SessionVisibility.Public);
            await setup.ExecuteAsync(new CancelSessionCommand(sessionId, 3, "Venue unavailable", "cancel", DateTimeOffset.UtcNow),
                "officer-2", CancellationToken.None);

            await Assert.ThrowsAsync<SessionSubscriptionConflictException>(() => store.SubscribeAsync(
                sessionId, accountId, 3, 0, "Email", "previously-read-consent", DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.Null(await store.GetAsync(sessionId, accountId, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(sessionId);
            await CleanupActorAsync(accountId);
        }
    }

    [IntakePostgresFact]
    public async Task Subscription_write_rejects_a_session_that_started_after_request_time()
    {
        var sessionId = Guid.NewGuid();
        const string accountId = "subscription-session-started";
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var setup = CreateService(db);
        var now = DateTimeOffset.UtcNow;
        try
        {
            await CreateActiveActorAsync(db, accountId);
            await PublishAsync(setup, sessionId, SessionVisibility.Public);
            await db.CommunitySessions.Where(item => item.SessionId == sessionId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.StartUtc, now.AddMinutes(-1)));

            var store = new PostgresEventSubscriptionStore(db);
            await Assert.ThrowsAsync<SessionSubscriptionConflictException>(() => store.SubscribeAsync(
                sessionId, accountId, 3, 0, "Email", "previously-read-consent", now.AddMinutes(-2), CancellationToken.None));
            Assert.Null(await store.GetAsync(sessionId, accountId, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(sessionId);
            await CleanupActorAsync(accountId);
        }
    }

    [IntakePostgresFact]
    public async Task Subscription_waiting_on_session_lock_rechecks_start_time_after_wait()
    {
        var sessionId = Guid.NewGuid();
        const string accountId = "subscription-started-during-lock";
        await using var writerDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await using var subscriberDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var setup = CreateService(writerDb);
        try
        {
            await CreateActiveActorAsync(writerDb, accountId);
            await PublishAsync(setup, sessionId, SessionVisibility.Public);
            await using var transaction = await writerDb.Database.BeginTransactionAsync();
            await writerDb.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE community_sessions SET start_utc = clock_timestamp() + interval '2 seconds' WHERE session_id = {sessionId}
                """);
            var pending = new PostgresEventSubscriptionStore(subscriberDb).SubscribeAsync(
                sessionId, accountId, 3, 0, "Email", "previously-read-consent", DateTimeOffset.UtcNow, CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(3));
            await transaction.CommitAsync();

            await Assert.ThrowsAsync<SessionSubscriptionConflictException>(() => pending);
            Assert.Null(await new PostgresEventSubscriptionStore(subscriberDb).GetAsync(sessionId, accountId, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(sessionId);
            await CleanupActorAsync(accountId);
        }
    }

    [IntakePostgresFact]
    public async Task Subscription_waits_for_concurrent_cancellation_and_rejects_stale_version()
    {
        var sessionId = Guid.NewGuid();
        const string accountId = "subscription-concurrent-cancellation";
        await using var writerDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await using var subscriberDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await using var observerDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var setup = CreateService(writerDb);
        try
        {
            await CreateActiveActorAsync(writerDb, accountId);
            await PublishAsync(setup, sessionId, SessionVisibility.Public);
            await subscriberDb.Database.OpenConnectionAsync();
            await using var pidCommand = subscriberDb.Database.GetDbConnection().CreateCommand();
            pidCommand.CommandText = "SELECT pg_backend_pid()";
            var subscriberPid = (int)(await pidCommand.ExecuteScalarAsync())!;

            await using var transaction = await writerDb.Database.BeginTransactionAsync();
            await writerDb.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE community_sessions SET status = 'Cancelled', version = version + 1 WHERE session_id = {sessionId}
                """);
            var pending = new PostgresEventSubscriptionStore(subscriberDb).SubscribeAsync(
                sessionId, accountId, 3, 0, "Email", "previously-read-consent", DateTimeOffset.UtcNow, CancellationToken.None);

            await observerDb.Database.OpenConnectionAsync();
            await using var blockedCommand = observerDb.Database.GetDbConnection().CreateCommand();
            blockedCommand.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock')";
            var parameter = blockedCommand.CreateParameter();
            parameter.ParameterName = "pid";
            parameter.Value = subscriberPid;
            blockedCommand.Parameters.Add(parameter);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var blocked = false;
            while (!blocked && DateTime.UtcNow < deadline)
            {
                blocked = (bool)(await blockedCommand.ExecuteScalarAsync())!;
                if (!blocked) await Task.Delay(25);
            }
            await transaction.CommitAsync();

            Assert.True(blocked, "The subscription did not wait on the session row lock.");
            await Assert.ThrowsAsync<SessionSubscriptionConflictException>(() => pending);
            Assert.Null(await new PostgresEventSubscriptionStore(subscriberDb).GetAsync(sessionId, accountId, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(sessionId);
            await CleanupActorAsync(accountId);
        }
    }

    [IntakePostgresFact]
    public async Task Event_subscription_dispatch_is_suppressed_when_session_is_cancelled()
    {
        var sessionId = Guid.NewGuid();
        const string accountId = "subscription-owner-cancel";
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var sessionStore = new PostgresCommunitySessionStore(db);
        var sessions = new CommunitySessionService(sessionStore, new FixtureMaterialEligibility());
        var service = new EventSubscriptionService(new PostgresEventSubscriptionStore(db), sessions, TimeProvider.System);
        var setup = CreateService(db);
        try
        {
            await CreateActiveActorAsync(db, accountId);
            await PublishAsync(setup, sessionId, SessionVisibility.Public);
            var options = Assert.IsType<EventSubscriptionOptions>(
                await service.GetOptionsAsync(sessionId, accountId, CancellationToken.None));
            await service.SubscribeAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "Email", CancellationToken.None);
            Assert.True(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "Email", CancellationToken.None));

            await setup.ExecuteAsync(new CancelSessionCommand(sessionId, options.SessionVersion,
                "Venue unavailable", "cancel for integration test", DateTimeOffset.UtcNow),
                "officer-1", CancellationToken.None);

            Assert.False(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "Email", CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(sessionId);
            await CleanupActorAsync(accountId);
        }
    }

    [IntakePostgresFact]
    public async Task CompletingSessionSuppressesPendingRemindersAndInvalidatesInProgressDelivery()
    {
        var sessionId = Guid.NewGuid();
        var first = $"session-complete-{Guid.NewGuid():N}";
        var second = $"session-complete-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var start = now.AddDays(3);
        var clock = new TestClock(now);
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var sessionStore = new PostgresCommunitySessionStore(db);
        var sessions = new CommunitySessionService(sessionStore, new FixtureMaterialEligibility(), clock);
        var subscriptions = new EventSubscriptionService(new PostgresEventSubscriptionStore(db), sessions, clock);
        try
        {
            await CreateActiveActorAsync(db, first);
            await CreateActiveActorAsync(db, second);
            await sessions.ExecuteAsync(CreateDraft(sessionId, "officer-1") with { StartUtc = start }, "officer-1", CancellationToken.None);
            await sessions.ExecuteAsync(new ReviewSessionCommand(sessionId, 1, false, "review", now), "officer-2", CancellationToken.None);
            await sessions.ExecuteAsync(new ScheduleSessionCommand(sessionId, 2, "schedule", now), "officer-2", CancellationToken.None);
            foreach (var account in new[] { first, second })
            {
                var options = Assert.IsType<EventSubscriptionOptions>(await subscriptions.GetOptionsAsync(sessionId, account, CancellationToken.None));
                await subscriptions.SubscribeAsync(sessionId, account, options.SessionVersion, options.ConsentReference, "Email", CancellationToken.None);
            }

            await db.SessionReminderOccurrences.Where(item => item.SessionId == sessionId).OrderBy(item => item.RemindAtUtc).Take(1)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.RemindAtUtc, now.AddMinutes(-1)));
            var deliveries = new PostgresSessionReminderDeliveryStore(db);
            await deliveries.MaterializeDueAsync(now, 10, CancellationToken.None);
            var claim = Assert.Single(await deliveries.ClaimDueAsync(now, TimeSpan.FromMinutes(1), 1, ["Email"], CancellationToken.None));
            clock.UtcNow = start.AddMinutes(1);
            await sessions.ExecuteAsync(new CompleteSessionCommand(sessionId, 3, "complete", now), "officer-2", CancellationToken.None);

            var stored = await db.SessionReminderDeliveries.AsNoTracking().Where(item => item.SessionId == sessionId).ToListAsync();
            Assert.Equal(2, stored.Count);
            Assert.Equal("OutcomeUnknown", stored.Single(item => item.DeliveryId == claim.DeliveryId).Status);
            Assert.Equal("Suppressed", stored.Single(item => item.DeliveryId != claim.DeliveryId).Status);
            Assert.False(await deliveries.IsLeaseCurrentAsync(claim.DeliveryId, claim.LeaseId, CancellationToken.None));
            Assert.All(await db.SessionReminderOccurrences.AsNoTracking().Where(item => item.SessionId == sessionId).ToListAsync(),
                occurrence => Assert.NotEqual("Pending", occurrence.Status));
        }
        finally
        {
            await CleanupAsync(sessionId);
            await CleanupActorAsync(first);
            await CleanupActorAsync(second);
        }
    }

    [IntakePostgresFact]
    public async Task Due_session_reminder_materializes_once_and_cancellation_invalidates_claim()
    {
        var sessionId = Guid.NewGuid();
        var accountId = $"session-reminder-{Guid.NewGuid():N}";
        var secondAccountId = $"session-reminder-{Guid.NewGuid():N}";
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var sessionStore = new PostgresCommunitySessionStore(db);
        var subscriptionService = new EventSubscriptionService(
            new PostgresEventSubscriptionStore(db), new CommunitySessionService(sessionStore, new FixtureMaterialEligibility()), TimeProvider.System);
        var setup = CreateService(db);
        try
        {
            await CreateActiveActorAsync(db, accountId);
            await CreateActiveActorAsync(db, secondAccountId);
            await PublishAsync(setup, sessionId, SessionVisibility.Public);
            var options = Assert.IsType<EventSubscriptionOptions>(
                await subscriptionService.GetOptionsAsync(sessionId, accountId, CancellationToken.None));
            var subscription = await subscriptionService.SubscribeAsync(
                sessionId, accountId, options.SessionVersion, options.ConsentReference, "Email", CancellationToken.None);
            var secondSubscription = await subscriptionService.SubscribeAsync(
                sessionId, secondAccountId, options.SessionVersion, options.ConsentReference, "Email", CancellationToken.None);
            var legacyConsent = $"session-reminder-consent-v1:{sessionId:N}:{options.SessionVersion}";
            await db.CommunitySessionSubscriptions
                .Where(item => item.SessionId == sessionId && item.AccountId == secondAccountId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.ConsentReference, legacyConsent));
            Assert.True(await subscriptionService.CanDispatchReminderAsync(sessionId, secondAccountId,
                options.SessionVersion, legacyConsent, "Email", CancellationToken.None));

            await db.SessionReminderOccurrences
                .Where(item => item.SessionId == sessionId)
                .OrderBy(item => item.RemindAtUtc)
                .Take(1)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    item => item.RemindAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));

            var deliveryStore = new PostgresSessionReminderDeliveryStore(db);
            var now = DateTimeOffset.UtcNow;
            Assert.Equal(1, await deliveryStore.MaterializeDueAsync(now, 1, CancellationToken.None));
            Assert.Equal(1, await deliveryStore.MaterializeDueAsync(now, 1, CancellationToken.None));
            Assert.Equal(0, await deliveryStore.MaterializeDueAsync(now, 10, CancellationToken.None));

            var deliveries = await db.SessionReminderDeliveries.AsNoTracking()
                .Where(item => item.SessionId == sessionId).ToListAsync();
            Assert.Equal(2, deliveries.Count);
            Assert.All(deliveries, delivery =>
            {
                Assert.Contains(delivery.AccountId, new[] { accountId, secondAccountId });
                Assert.Equal("Email", delivery.Channel);
                Assert.Equal(options.SessionVersion, delivery.SessionVersion);
                Assert.Equal(delivery.AccountId == secondAccountId ? legacyConsent : options.ConsentReference,
                    delivery.ConsentReference);
                Assert.Equal("Pending", delivery.Status);
            });
            Assert.Equal(subscription.Version, deliveries.Single(item => item.AccountId == accountId).SubscriptionVersion);
            Assert.Equal(secondSubscription.Version, deliveries.Single(item => item.AccountId == secondAccountId).SubscriptionVersion);

            await using var competingDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
            var claims = await Task.WhenAll(
                deliveryStore.ClaimDueAsync(now, TimeSpan.FromMinutes(1), 10, ["Email"], CancellationToken.None),
                new PostgresSessionReminderDeliveryStore(competingDb).ClaimDueAsync(
                    now, TimeSpan.FromMinutes(1), 10, ["Email"], CancellationToken.None));
            var claimed = claims.SelectMany(batch => batch).ToArray();
            Assert.Equal(2, claimed.Length);
            Assert.Empty(await deliveryStore.ClaimDueAsync(
                now, TimeSpan.FromMinutes(1), 10, ["Email"], CancellationToken.None));

            await setup.ExecuteAsync(new CancelSessionCommand(
                    sessionId, options.SessionVersion, "Venue unavailable", "cancel for integration test", now),
                "officer-1", CancellationToken.None);

            var suppressed = await db.SessionReminderDeliveries.AsNoTracking()
                .Where(item => item.SessionId == sessionId).ToListAsync();
            Assert.All(suppressed, item =>
            {
                Assert.Equal("OutcomeUnknown", item.Status);
                Assert.Null(item.LeaseId);
            });
            foreach (var claim in claimed)
            {
                Assert.False(await deliveryStore.CompleteAsync(
                    claim.DeliveryId, claim.LeaseId, SessionReminderDeliveryStatus.Submitted, now,
                    CancellationToken.None));
            }
        }
        finally
        {
            await CleanupAsync(sessionId);
            await CleanupActorAsync(accountId);
            await CleanupActorAsync(secondAccountId);
        }
    }

    [IntakePostgresFact]
    public async Task Expired_in_progress_session_delivery_becomes_unknown_without_retry()
    {
        var sessionId = Guid.NewGuid();
        var accountId = $"session-reminder-expired-{Guid.NewGuid():N}";
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var setup = CreateService(db);
        try
        {
            await CreateActiveActorAsync(db, accountId);
            await PublishAsync(setup, sessionId, SessionVisibility.Public);
            var sessionStore = new PostgresCommunitySessionStore(db);
            var service = new EventSubscriptionService(
                new PostgresEventSubscriptionStore(db), new CommunitySessionService(sessionStore, new FixtureMaterialEligibility()), TimeProvider.System);
            var options = Assert.IsType<EventSubscriptionOptions>(
                await service.GetOptionsAsync(sessionId, accountId, CancellationToken.None));
            await service.SubscribeAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "Email", CancellationToken.None);
            await db.SessionReminderOccurrences.Where(item => item.SessionId == sessionId).Take(1)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    item => item.RemindAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));

            var now = DateTimeOffset.UtcNow;
            var clock = new TestClock(now);
            var deliveryStore = new PostgresSessionReminderDeliveryStore(db, clock);
            Assert.Equal(1, await deliveryStore.MaterializeDueAsync(now, 10, CancellationToken.None));
            var lease = Assert.Single(await deliveryStore.ClaimDueAsync(
                now, TimeSpan.FromMinutes(1), 10, ["Email"], CancellationToken.None));

            Assert.True(await deliveryStore.IsLeaseCurrentAsync(lease.DeliveryId, lease.LeaseId, CancellationToken.None));
            clock.UtcNow = lease.LeaseUntilUtc;
            Assert.False(await deliveryStore.IsLeaseCurrentAsync(lease.DeliveryId, lease.LeaseId, CancellationToken.None));

            Assert.Empty(await deliveryStore.ClaimDueAsync(
                now.AddMinutes(2), TimeSpan.FromMinutes(1), 10, ["Email"], CancellationToken.None));

            var delivery = await db.SessionReminderDeliveries.AsNoTracking()
                .SingleAsync(item => item.DeliveryId == lease.DeliveryId);
            Assert.Equal("OutcomeUnknown", delivery.Status);
            Assert.Null(delivery.LeaseId);
            Assert.False(await deliveryStore.CompleteAsync(lease.DeliveryId, lease.LeaseId,
                SessionReminderDeliveryStatus.Submitted, now.AddMinutes(2), CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(sessionId);
            await CleanupActorAsync(accountId);
        }
    }

    private static async Task CreateActiveActorAsync(SwiftAid.Infrastructure.Persistence.SwiftAidDbContext db, string actorId)
    {
        var now = DateTimeOffset.UtcNow;
        db.Actors.Add(new SwiftAid.Infrastructure.Persistence.ActorEntity
        {
            ActorId = actorId,
            Status = SwiftAid.Infrastructure.Persistence.PersistentActorStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        db.ActorAccessStates.Add(new SwiftAid.Infrastructure.Persistence.ActorAccessStateEntity
        {
            ActorId = actorId,
            AccessEpoch = 0,
            Version = 1,
            UpdatedAtUtc = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task CleanupActorAsync(string actorId)
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var accessState = await db.ActorAccessStates.SingleOrDefaultAsync(item => item.ActorId == actorId);
        if (accessState is not null) db.ActorAccessStates.Remove(accessState);
        var actor = await db.Actors.SingleOrDefaultAsync(item => item.ActorId == actorId);
        if (actor is not null) db.Actors.Remove(actor);
        await db.SaveChangesAsync();
    }

    private static TestCommunitySessionService CreateService(SwiftAid.Infrastructure.Persistence.SwiftAidDbContext db)
    {
        var store = new PostgresCommunitySessionStore(db);
        return new TestCommunitySessionService(new CommunitySessionService(store, new FixtureMaterialEligibility()), store);
    }

    private static async Task PublishAsync(TestCommunitySessionService service, Guid sessionId, SessionVisibility visibility)
    {
        var draft = CreateDraft(sessionId, "officer-1") with { Visibility = visibility };
        await service.ExecuteAsync(draft, "officer-1", CancellationToken.None);
        await service.ExecuteAsync(
            new ReviewSessionCommand(sessionId, 1, false, "reviewed", DateTimeOffset.UtcNow),
            "officer-2",
            CancellationToken.None);
        await service.ExecuteAsync(
            new ScheduleSessionCommand(sessionId, 2, "publish", DateTimeOffset.UtcNow),
            "officer-2",
            CancellationToken.None);
    }

    private static CreateSessionDraftCommand CreateDraft(Guid sessionId, string creator) =>
        new(
            sessionId,
            "Malaria prevention talk",
            "An evening talk on bed nets and stagnant water.",
            "Adults in the Budiriro catchment",
            ["malaria", "prevention"],
            "ZW",
            "harare",
            "HumanHealth",
            "Nurse Moyo",
            "en",
            DateTimeOffset.UtcNow.AddDays(7),
            HarareZoneId(),
            60,
            50,
            [new SessionMaterialRef("card-1", "v3")],
            SessionVisibility.Public,
            SessionChannelType.WhatsAppGroup,
            "https://chat.example/join/abc",
            [24, 1],
            "test",
            DateTimeOffset.UtcNow);

    private static UpdateSessionCommand UpdateDraft(Guid sessionId, long expectedVersion, string title) =>
        new(
            sessionId,
            expectedVersion,
            title,
            "An evening talk on bed nets and stagnant water.",
            "Adults in the Budiriro catchment",
            ["malaria", "prevention"],
            "Nurse Moyo",
            "en",
            DateTimeOffset.UtcNow.AddDays(8),
            90,
            50,
            [new SessionMaterialRef("card-1", "v3")],
            SessionVisibility.Public,
            SessionChannelType.WhatsAppGroup,
            "https://chat.example/join/abc",
            [24, 1],
            "test update",
            DateTimeOffset.UtcNow);

    private static string HarareZoneId()
    {
        foreach (var candidate in new[] { "Africa/Harare", "South Africa Standard Time" })
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(candidate);
                return candidate;
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException("No Harare UTC+2 zone is available on this host.");
    }

    private static async Task CleanupAsync(params Guid[] sessionIds)
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        foreach (var sessionId in sessionIds)
        {
            var deliveries = await db.SessionReminderDeliveries
                .Where(item => item.SessionId == sessionId).ToListAsync();
            db.SessionReminderDeliveries.RemoveRange(deliveries);
            var subscriptions = await db.CommunitySessionSubscriptions
                .Where(item => item.SessionId == sessionId).ToListAsync();
            db.CommunitySessionSubscriptions.RemoveRange(subscriptions);
            var occurrences = await db.SessionReminderOccurrences
                .Where(item => item.SessionId == sessionId).ToListAsync();
            db.SessionReminderOccurrences.RemoveRange(occurrences);
            var revisions = await db.CommunitySessionRevisions
                .Where(item => item.SessionId == sessionId).ToListAsync();
            db.CommunitySessionRevisions.RemoveRange(revisions);
            var session = await db.CommunitySessions
                .SingleOrDefaultAsync(item => item.SessionId == sessionId);
            if (session is not null)
            {
                db.CommunitySessions.Remove(session);
            }
        }

        await db.SaveChangesAsync();
    }

    // Synthetic fixture only: production does not have an eligibility authority wired.
    private sealed class HistoryAuthorizer(Guid allowedSession, string allowedActor) : ICommunitySessionHistoryAuthorizer
    {
        public bool RevokeAfterNextCheck { get; set; }
        private bool _revoked;
        public Task<bool> CanReadAsync(string actorId, Guid sessionId, CancellationToken cancellationToken)
        {
            var allowed = !_revoked && sessionId == allowedSession && actorId == allowedActor;
            if (RevokeAfterNextCheck)
            {
                _revoked = true;
                RevokeAfterNextCheck = false;
            }
            return Task.FromResult(allowed);
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FixtureMaterialEligibility : ISessionMaterialEligibility
    {
        public Task<bool> AreEligibleAsync(SessionMaterialEligibilityRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    /// <summary>Test-local handle exposing the store counters alongside the service.</summary>
    private sealed class TestCommunitySessionService(ICommunitySessionService service, ICommunitySessionStore store)
    {
        private readonly ICommunitySessionStore _store = store;

        public Task<SessionCommandResult> ExecuteAsync(SessionCommand command, string actorId, CancellationToken ct) =>
            service.ExecuteAsync(command, actorId, ct);

        public Task<CommunitySessionView?> GetStaffAsync(Guid sessionId, string actorId, CancellationToken ct) =>
            service.GetStaffAsync(sessionId, actorId, ct);

        public Task<IReadOnlyList<PublicSessionView>> ListPublicAsync(int? limit, CancellationToken ct) =>
            service.ListPublicAsync(limit, ct);

        public Task<PublicSessionView?> GetPublicAsync(Guid sessionId, CancellationToken ct) =>
            service.GetPublicAsync(sessionId, ct);

        public Task<string?> GetIcsAsync(Guid sessionId, DateTimeOffset generatedAtUtc, CancellationToken ct) =>
            service.GetIcsAsync(sessionId, generatedAtUtc, ct);

        public Task<string?> GetInvitationTextAsync(Guid sessionId, CancellationToken ct) =>
            service.GetInvitationTextAsync(sessionId, ct);

        public Task<int> StoreCountRevisionsAsync(Guid sessionId, CancellationToken ct) =>
            _store.CountRevisionsAsync(sessionId, ct);

        public Task<int> StoreCountPendingRemindersAsync(Guid sessionId, CancellationToken ct) =>
            _store.CountPendingRemindersAsync(sessionId, ct);

        public Task<int> StoreCountSuppressedRemindersAsync(Guid sessionId, CancellationToken ct) =>
            _store.CountSuppressedRemindersAsync(sessionId, ct);
    }
}
