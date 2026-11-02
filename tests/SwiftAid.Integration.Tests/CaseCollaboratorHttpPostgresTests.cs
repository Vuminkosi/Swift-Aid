using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task Collaborator_http_commands_use_real_authority_receipts_and_persistent_revocation()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        cleanup.TrackTargetGrant(await AddEligibleTargetGrantAsync(fixture));
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var protection = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), protection);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication()
            .AddScheme<CollaboratorTestAuthenticationOptions, CollaboratorTestAuthenticationHandler>(
                IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<CollaboratorTestAuthenticationOptions, CollaboratorTestAuthenticationHandler>(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, options =>
                {
                    options.Principals["officer"] = Principal(fixture.ActorId, fixture.AccessEpoch);
                    options.Principals["recipient"] = Principal(fixture.NewOwnerActorId, fixture.AccessEpoch + 1);
                    options.Principals["recipient-old"] = Principal(fixture.NewOwnerActorId, fixture.AccessEpoch);
                    options.Principals["recipient-refreshed"] = Principal(fixture.NewOwnerActorId, fixture.AccessEpoch + 2);
                });
        builder.Services.AddAuthorization();
        builder.Services.AddScoped(_ => fixture.CreateDbContext());
        builder.Services.AddScoped<ICaseCommandProcessor>(services =>
        {
            var db = services.GetRequiredService<SwiftAidDbContext>();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            var payloads = new PostgresProtectedCaseEventPayloadStore(db, unit, keyRing.CreateOptions(), protection);
            var persistence = new PostgresCaseCommandPersistence(db, unit, payloads, new CaseEventSerializer());
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
            return new CaseCommandProcessor(unit, policy, persistence);
        });
        builder.Services.AddScoped<ICaseTimelineQuery>(services =>
        {
            var db = services.GetRequiredService<SwiftAidDbContext>();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
            return new CaseTimelineQuery(new PostgresCaseTimelineStore(db), policy, unit);
        });
        builder.Services.AddScoped<ICaseCollaboratorCandidatesQuery>(services =>
        {
            var db = services.GetRequiredService<SwiftAidDbContext>();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
            return new CaseCollaboratorCandidatesQuery(new PostgresCaseCollaboratorCandidatesStore(db), policy, unit);
        });
        await using var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapCaseCommandEndpoints();
        application.MapCaseTimelineEndpoints();
        await application.StartAsync();
        using var client = application.GetTestClient();
        async Task AssertReadStatus(string token, HttpStatusCode expected)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{fixture.CaseId:D}/timeline");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Synthetic private", body);
            Assert.DoesNotContain("synthetic protected report text", body);
            if (expected == HttpStatusCode.OK && token == "recipient")
            {
                var timeline = await response.Content.ReadFromJsonAsync<CaseTimeline>();
                Assert.Null(timeline!.Collaboration);
            }
        }

        async Task AssertManagementView(bool revoked)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{fixture.CaseId:D}/timeline?limit=1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "officer");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var timeline = Assert.IsType<CaseTimeline>(await response.Content.ReadFromJsonAsync<CaseTimeline>());
            Assert.True(timeline.Collaboration!.CanGrant);
            var share = Assert.Single(timeline.Collaboration.Grants);
            Assert.Equal(fixture.NewOwnerActorId, share.ActorId);
            Assert.Null(share.SafeActorDisplayLabel);
            Assert.Equal(["ReadCase"], share.Operations.ToArray());
            Assert.Equal(revoked, share.IsRevoked);
            Assert.Equal(revoked ? "Collaborator access revoked" : "Collaborator access granted", Assert.Single(timeline.Entries).Label);
        }

        await AssertReadStatus("recipient-old", HttpStatusCode.NotFound);
        async Task AssertCandidates(HttpStatusCode expected, int count = 0, string suffix = "")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{fixture.CaseId:D}/collaborator-candidates{suffix}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "officer");
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.OK)
            {
                var page = await response.Content.ReadFromJsonAsync<CaseCollaboratorCandidatePage>();
                Assert.Equal(count, page!.Items.Count);
                Assert.All(page.Items, item => Assert.Equal(fixture.NewOwnerActorId, item.ActorId));
            }
        }
        await AssertCandidates(HttpStatusCode.NotFound);

        var grant = new CaseCommandApiRequest(1, "GrantCaseCollaborator", Guid.NewGuid(), 2, null,
            "Synthetic private grant reason", null, null, null, null,
            GrantId: Guid.NewGuid(), CollaboratorActorId: fixture.NewOwnerActorId,
            CollaborationActions: CaseCollaborationActions.ReadCase, ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        async Task<HttpResponseMessage> Send(CaseCommandApiRequest body, string? token = "officer")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cases/{fixture.CaseId:D}/commands")
            {
                Content = JsonContent.Create(body)
            };
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return await client.SendAsync(request);
        }

        using (var anonymous = await Send(grant, null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        using (var denied = await Send(grant))
        {
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.DoesNotContain(fixture.NewOwnerActorId, await denied.Content.ReadAsStringAsync());
        }
        await AddOperationAsync(fixture, CaseOperation.ManageCollaborators);
        await AssertCandidates(HttpStatusCode.OK, 1);
        await AssertCandidates(HttpStatusCode.BadRequest, suffix: "?limit=51");
        using (var invalid = await Send(grant with { CollaborationActions = (CaseCollaborationActions)64 }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var deniedScope = await Send(grant with { CollaborationActions = CaseCollaborationActions.ReadCase | CaseCollaborationActions.ManageFollowUp }))
        {
            Assert.Equal(HttpStatusCode.NotFound, deniedScope.StatusCode);
        }

        CaseCommandApiResponse receipt;
        using (var accepted = await Send(grant))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            receipt = Assert.IsType<CaseCommandApiResponse>(await accepted.Content.ReadFromJsonAsync<CaseCommandApiResponse>());
            Assert.Equal(3, receipt.AggregateVersion);
            Assert.Single(receipt.EventIds);
        }
        using (var replay = await Send(grant))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var repeated = Assert.IsType<CaseCommandApiResponse>(await replay.Content.ReadFromJsonAsync<CaseCommandApiResponse>());
            Assert.Equal(receipt.EventIds.ToArray(), repeated.EventIds.ToArray());
        }
        await AssertReadStatus("recipient-old", HttpStatusCode.NotFound);
        await AssertReadStatus("recipient", HttpStatusCode.OK);
        await AssertManagementView(false);
        await AssertCandidates(HttpStatusCode.OK);
        using (var changedReplay = await Send(grant with { Reason = "Changed after acceptance" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
        }

        var revoke = new CaseCommandApiRequest(1, "RevokeCaseCollaborator", Guid.NewGuid(), 3, null,
            "Synthetic private revocation reason", null, null, null, null, GrantId: grant.GrantId);
        using (var recipientDenied = await Send(revoke, "recipient"))
        {
            Assert.Equal(HttpStatusCode.NotFound, recipientDenied.StatusCode);
        }
        using (var revoked = await Send(revoke))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            Assert.Equal(4, (await revoked.Content.ReadFromJsonAsync<CaseCommandApiResponse>())!.AggregateVersion);
        }
        using (var replay = await Send(revoke))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }
        await AssertReadStatus("recipient", HttpStatusCode.NotFound);
        await AssertReadStatus("recipient-refreshed", HttpStatusCode.NotFound);
        await AssertManagementView(true);
        await using var verification = fixture.CreateDbContext();
        var share = await verification.CaseShares.SingleAsync(item => item.CaseId == fixture.CaseId);
        Assert.Equal(grant.GrantId, share.GrantId);
        Assert.False(share.IsActive);
        Assert.Null(share.ChangeReason);
        Assert.Equal(fixture.AccessEpoch + 2, (await verification.ActorAccessStates.SingleAsync(item => item.ActorId == fixture.NewOwnerActorId)).AccessEpoch);
        Assert.Equal(2, await verification.CommandReceipts.CountAsync(item => item.CommandId == grant.CommandId || item.CommandId == revoke.CommandId));
        Assert.Equal(2, await verification.CaseEvents.CountAsync(item => item.CommandId == grant.CommandId || item.CommandId == revoke.CommandId));
        Assert.Equal(fixture.ActorId, (await verification.ManagedCases.SingleAsync(item => item.CaseId == fixture.CaseId)).AccountableOwnerActorId);
    }

    private static ClaimsPrincipal Principal(string actorId, long epoch) => new(new ClaimsIdentity([
        new Claim("sub", actorId),
        new Claim(ActorContext.AccessEpochClaimType, epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
    ], OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme));
}

internal sealed class CollaboratorTestAuthenticationOptions : AuthenticationSchemeOptions
{
    public Dictionary<string, ClaimsPrincipal> Principals { get; } = [];
}

internal sealed class CollaboratorTestAuthenticationHandler(
    IOptionsMonitor<CollaboratorTestAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<CollaboratorTestAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !Options.Principals.TryGetValue(header[7..], out var principal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
