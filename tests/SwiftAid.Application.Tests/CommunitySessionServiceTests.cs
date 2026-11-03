using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Sessions;
using DomainSessions = SwiftAid.Domain.Sessions;

namespace SwiftAid.Application.Tests;

/// <summary>P10.1 service unit tests: dispatch, version conflicts, and public gating.</summary>
public sealed class CommunitySessionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Create_then_duplicate_create_conflicts()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        var created = await services.Service.ExecuteAsync(CreateDraft(id), "officer-1", CancellationToken.None);
        Assert.Equal(1, created.Version);

        var conflict = await Assert.ThrowsAsync<SessionVersionConflictException>(() =>
            services.Service.ExecuteAsync(CreateDraft(id), "officer-1", CancellationToken.None));
        Assert.Equal(0, conflict.ExpectedVersion);
        Assert.Equal(1, conflict.ActualVersion);
    }

    [Fact]
    public async Task Create_with_nonzero_expected_version_conflicts()
    {
        var services = CreateServices();
        var conflict = await Assert.ThrowsAsync<SessionVersionConflictException>(() =>
            services.Service.ExecuteAsync(
                CreateDraft(Guid.NewGuid()) with { ExpectedVersion = 3 },
                "officer-1",
                CancellationToken.None));
        Assert.Equal(3, conflict.ExpectedVersion);
        Assert.Equal(0, conflict.ActualVersion);
    }

    [Fact]
    public async Task Stale_expected_version_conflicts_with_actual()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await services.Service.ExecuteAsync(CreateDraft(id), "officer-1", CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<SessionVersionConflictException>(() =>
            services.Service.ExecuteAsync(
                new ReviewSessionCommand(id, 7, false, "stale", Now),
                "officer-2",
                CancellationToken.None));
        Assert.Equal(7, conflict.ExpectedVersion);
        Assert.Equal(1, conflict.ActualVersion);
    }

    [Fact]
    public async Task Command_for_unknown_session_throws_not_found()
    {
        var services = CreateServices();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            services.Service.ExecuteAsync(
                new ReviewSessionCommand(Guid.NewGuid(), 1, false, "missing", Now),
                "officer-2",
                CancellationToken.None));
    }

    [Fact]
    public async Task Unsupported_command_throws()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await services.Service.ExecuteAsync(CreateDraft(id), "officer-1", CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            services.Service.ExecuteAsync(new BogusCommand(id, 1, "bogus", Now), "officer-1", CancellationToken.None));
    }

    [Fact]
    public async Task Review_schedule_and_cancel_flow_records_reminder_sync()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await services.Service.ExecuteAsync(CreateDraft(id), "officer-1", CancellationToken.None);
        await services.Service.ExecuteAsync(
            new ReviewSessionCommand(id, 1, false, "reviewed", Now), "officer-2", CancellationToken.None);
        await services.Service.ExecuteAsync(
            new ScheduleSessionCommand(id, 2, "publish", Now), "officer-2", CancellationToken.None);

        Assert.Equal(SessionReminderSync.Recompute, services.Store.LastSync);
        Assert.Equal(2, services.Store.LastOccurrences.Count);

        await services.Service.ExecuteAsync(
            new CancelSessionCommand(id, 3, "Venue flooded", "cancel", Now), "officer-2", CancellationToken.None);
        Assert.Equal(SessionReminderSync.SuppressPending, services.Store.LastSync);
        Assert.Equal(4, services.Store.Revisions);
    }

    [Fact]
    public async Task InvitationForDisplayedVersionRejectsARevision()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await PublishAsync(services.Service, id, SessionVisibility.Public);
        Assert.NotNull(await services.Service.GetInvitationTextForVersionAsync(id, 3, CancellationToken.None));
        await services.Service.ExecuteAsync(UpdateDraft(id, 3) with { Title = "Malaria prevention talk" }, "officer-2", CancellationToken.None);

        Assert.Null(await services.Service.GetInvitationTextForVersionAsync(id, 3, CancellationToken.None));
        Assert.NotNull(await services.Service.GetInvitationTextForVersionAsync(id, 4, CancellationToken.None));
        Assert.NotNull(await services.Service.GetInvitationTextAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task CompletedPublishedSessionsRemainDiscoverableButInternalSessionsDoNot()
    {
        var services = CreateServices();
        var publicId = Guid.NewGuid();
        var internalId = Guid.NewGuid();
        await PublishAsync(services.Service, publicId, SessionVisibility.Public);
        await PublishAsync(services.Service, internalId, SessionVisibility.Internal);
        await services.Service.ExecuteAsync(new CompleteSessionCommand(publicId, 3, "completed", Start.AddHours(1)), "officer-2", CancellationToken.None);
        await services.Service.ExecuteAsync(new CompleteSessionCommand(internalId, 3, "completed", Start.AddHours(1)), "officer-2", CancellationToken.None);

        var detail = await services.Service.GetPublicAsync(publicId, CancellationToken.None);
        var list = await services.Service.ListPublicAsync(null, CancellationToken.None);
        var query = await services.Service.QueryPublicAsync(new PublicSessionQuery(), CancellationToken.None);
        Assert.Equal("Completed", detail!.Status);
        Assert.Null(detail.ChannelLink);
        Assert.NotNull((await services.Service.GetStaffAsync(publicId, "officer-2", CancellationToken.None))!.ChannelLink);
        Assert.Null(await services.Service.GetInvitationTextAsync(publicId, CancellationToken.None));
        Assert.DoesNotContain("https://chat.example/join/abc", (await services.Service.GetIcsAsync(publicId, Start.AddHours(2), CancellationToken.None))!, StringComparison.Ordinal);
        Assert.Contains(list, item => item.SessionId == publicId && item.Status == "Completed");
        Assert.Null(Assert.Single(list, item => item.SessionId == publicId).ChannelLink);
        Assert.Contains(query.Items, item => item.SessionId == publicId && item.Status == "Completed");
        Assert.Null(Assert.Single(query.Items, item => item.SessionId == publicId).ChannelLink);
        Assert.DoesNotContain(list, item => item.SessionId == internalId);
        Assert.DoesNotContain(query.Items, item => item.SessionId == internalId);
        Assert.Single((await services.Service.QueryPublicAsync(new PublicSessionQuery(Status: "completed"), CancellationToken.None)).Items);
        Assert.Empty((await services.Service.QueryPublicAsync(new PublicSessionQuery(Status: "Scheduled"), CancellationToken.None)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => services.Service.QueryPublicAsync(new PublicSessionQuery(Status: "Draft"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => services.Service.QueryPublicAsync(new PublicSessionQuery(Status: "Reviewed"), CancellationToken.None));
    }

    [Fact]
    public async Task Update_on_draft_does_not_touch_reminders()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await services.Service.ExecuteAsync(CreateDraft(id), "officer-1", CancellationToken.None);
        await services.Service.ExecuteAsync(UpdateDraft(id, 1), "officer-1", CancellationToken.None);
        Assert.Equal(SessionReminderSync.None, services.Store.LastSync);
    }

    [Fact]
    public async Task EditingAfterReviewRequiresAReviewOfTheNewVersion()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await services.Service.ExecuteAsync(CreateDraft(id), "officer-1", CancellationToken.None);
        await services.Service.ExecuteAsync(new ReviewSessionCommand(id, 1, false, "review", Now), "officer-2", CancellationToken.None);
        var edited = await services.Service.ExecuteAsync(UpdateDraft(id, 2), "officer-1", CancellationToken.None);
        Assert.Equal("Draft", edited.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.Service.ExecuteAsync(
            new ScheduleSessionCommand(id, 3, "schedule", Now), "officer-2", CancellationToken.None));
        await services.Service.ExecuteAsync(new ReviewSessionCommand(id, 3, false, "review update", Now), "officer-2", CancellationToken.None);
        var scheduled = await services.Service.ExecuteAsync(new ScheduleSessionCommand(id, 4, "schedule", Now), "officer-2", CancellationToken.None);
        Assert.Equal("Scheduled", scheduled.Status);
    }

    [Fact]
    public async Task EligibilityIsRecheckedBetweenReviewAndScheduling()
    {
        var store = new FakeSessionStore();
        var eligibility = new FixtureMaterialEligibility();
        var service = new CommunitySessionService(store, eligibility);
        var id = Guid.NewGuid();
        await service.ExecuteAsync(CreateDraft(id), "officer-1", CancellationToken.None);
        await service.ExecuteAsync(new ReviewSessionCommand(id, 1, false, "review", Now), "officer-2", CancellationToken.None);
        eligibility.Eligible = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(new ScheduleSessionCommand(id, 2, "schedule", Now), "officer-2", CancellationToken.None));

        var unchanged = await service.GetStaffAsync(id, "officer-1", CancellationToken.None);
        Assert.Equal("Reviewed", unchanged!.Status);
        Assert.Equal(2, unchanged.Version);
    }

    [Fact]
    public async Task WithdrawnMaterialBlocksScheduledRevisionButNotCancellation()
    {
        var store = new FakeSessionStore();
        var eligibility = new FixtureMaterialEligibility();
        var service = new CommunitySessionService(store, eligibility);
        var id = Guid.NewGuid();
        await PublishAsync(service, id, SessionVisibility.Public);
        Assert.Equal("harare", eligibility.LastRequest!.RegionId);
        Assert.Equal("en", eligibility.LastRequest.Language);
        Assert.Single(eligibility.LastRequest.Materials);
        eligibility.Eligible = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(UpdateDraft(id, 3), "officer-1", CancellationToken.None));
        var unchanged = await service.GetStaffAsync(id, "officer-1", CancellationToken.None);
        Assert.Equal(3, unchanged!.Version);
        Assert.Equal("Scheduled", unchanged.Status);
        var cancelled = await service.ExecuteAsync(new CancelSessionCommand(id, 3, "No longer safe", "cancel", Now), "officer-2", CancellationToken.None);
        Assert.Equal("Cancelled", cancelled.Status);
    }

    [Fact]
    public async Task PublishedContentCannotChangeWithoutReviewButReschedulingKeepsTheReviewedContent()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await PublishAsync(services.Service, id, SessionVisibility.Public);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.Service.ExecuteAsync(UpdateDraft(id, 3), "officer-1", CancellationToken.None));
        var unchanged = await services.Service.GetPublicAsync(id, CancellationToken.None);
        Assert.Equal("Malaria prevention talk", unchanged!.Title);
        Assert.Equal(Start, unchanged.StartUtc);
        Assert.Equal(3, unchanged.Version);

        var result = await services.Service.ExecuteAsync(
            UpdateDraft(id, 3) with { Title = unchanged.Title }, "officer-1", CancellationToken.None);
        var rescheduled = await services.Service.GetPublicAsync(id, CancellationToken.None);
        Assert.Equal(4, result.Version);
        Assert.Equal(Start.AddDays(1), rescheduled!.StartUtc);
        Assert.Equal(unchanged.Title, rescheduled.Title);
        Assert.Equal(SessionReminderSync.Recompute, services.Store.LastSync);
    }

    [Fact]
    public async Task RescheduleDoesNotRecreateRemindersAlreadyDueAtRevisionTime()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await PublishAsync(services.Service, id, SessionVisibility.Public);
        var revisionTime = Start.AddHours(-2);
        await services.Service.ExecuteAsync(UpdateDraft(id, 3) with
        {
            Title = "Malaria prevention talk",
            StartUtc = Start.AddMinutes(30),
            RecordedAtUtc = revisionTime
        }, "officer-1", CancellationToken.None);

        var occurrence = Assert.Single(services.Store.LastOccurrences);
        Assert.Equal(1, occurrence.LeadHours);
        Assert.True(occurrence.RemindAtUtc > revisionTime);
    }

    [Fact]
    public async Task SchedulingFailsClosedWithoutMaterialEligibilityAuthority()
    {
        var store = new FakeSessionStore();
        var service = new CommunitySessionService(store);
        var approvedFixture = new CommunitySessionService(store, new FixtureMaterialEligibility());
        var id = Guid.NewGuid();
        await service.ExecuteAsync(CreateDraft(id), "officer-1", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(new ReviewSessionCommand(id, 1, false, "review", Now), "officer-2", CancellationToken.None));
        await approvedFixture.ExecuteAsync(new ReviewSessionCommand(id, 1, false, "review", Now), "officer-2", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(new ScheduleSessionCommand(id, 2, "publish", Now), "officer-2", CancellationToken.None));

        var draft = await service.GetStaffAsync(id, "officer-1", CancellationToken.None);
        Assert.Equal("Reviewed", draft!.Status);
        Assert.Equal(2, draft.Version);
        Assert.Null(await service.GetPublicAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task PublicReadsOmitUnsafeLegacyDestinationButStaffCanInspectIt()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await PublishAsync(services.Service, id, SessionVisibility.Public);
        var session = (await services.Store.LoadAsync(id, CancellationToken.None))!;
        const string unsafeLink = "https://user:password@example.test/session";
        typeof(DomainSessions.CommunitySession).GetField("<ChannelLink>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, unsafeLink);

        Assert.Equal(unsafeLink, (await services.Service.GetStaffAsync(id, "officer-1", CancellationToken.None))!.ChannelLink);
        Assert.Null((await services.Service.GetPublicAsync(id, CancellationToken.None))!.ChannelLink);
        Assert.Null(Assert.Single(await services.Service.ListPublicAsync(null, CancellationToken.None)).ChannelLink);
        Assert.Null(Assert.Single((await services.Service.QueryPublicAsync(new PublicSessionQuery(), CancellationToken.None)).Items).ChannelLink);
        Assert.DoesNotContain("password", (await services.Service.GetIcsAsync(id, Now, CancellationToken.None))!, StringComparison.Ordinal);
        Assert.DoesNotContain("password", (await services.Service.GetInvitationTextAsync(id, CancellationToken.None))!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicOutputsDoNotExposeUnverifiedMaterialReferences()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await PublishAsync(services.Service, id, SessionVisibility.Public);

        var staff = await services.Service.GetStaffAsync(id, "officer-1", CancellationToken.None);
        var publicView = await services.Service.GetPublicAsync(id, CancellationToken.None);
        var list = await services.Service.ListPublicAsync(null, CancellationToken.None);
        var page = await services.Service.QueryPublicAsync(new PublicSessionQuery(), CancellationToken.None);
        var calendar = await services.Service.GetIcsAsync(id, Now, CancellationToken.None);
        var invitation = await services.Service.GetInvitationTextAsync(id, CancellationToken.None);

        var material = Assert.Single(staff!.Materials);
        Assert.Empty(publicView!.Materials);
        Assert.Empty(Assert.Single(list).Materials);
        Assert.Empty(Assert.Single(page.Items).Materials);
        Assert.DoesNotContain(material.MaterialId, calendar!, StringComparison.Ordinal);
        Assert.DoesNotContain(material.MaterialId, invitation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_gating_hides_internal_and_draft_sessions()
    {
        var services = CreateServices();
        var publicId = Guid.NewGuid();
        var internalId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        await PublishAsync(services.Service, publicId, SessionVisibility.Public);
        await PublishAsync(services.Service, internalId, SessionVisibility.Internal);
        await services.Service.ExecuteAsync(CreateDraft(draftId), "officer-1", CancellationToken.None);

        var listed = await services.Service.ListPublicAsync(null, CancellationToken.None);
        Assert.Contains(listed, item => item.SessionId == publicId);
        Assert.DoesNotContain(listed, item => item.SessionId == internalId);

        Assert.NotNull(await services.Service.GetPublicAsync(publicId, CancellationToken.None));
        Assert.Null(await services.Service.GetPublicAsync(internalId, CancellationToken.None));
        Assert.Null(await services.Service.GetPublicAsync(draftId, CancellationToken.None));

        var ics = await services.Service.GetIcsAsync(publicId, Now, CancellationToken.None);
        Assert.NotNull(ics);
        Assert.Contains("SEQUENCE:0", ics, StringComparison.Ordinal);
        Assert.Null(await services.Service.GetIcsAsync(draftId, Now, CancellationToken.None));
        Assert.Null(await services.Service.GetInvitationTextAsync(draftId, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            services.Service.ListPublicAsync(0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            services.Service.ListPublicAsync(101, CancellationToken.None));
    }

    [Fact]
    public async Task Blank_actor_is_rejected()
    {
        var services = CreateServices();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            services.Service.ExecuteAsync(CreateDraft(Guid.NewGuid()), " ", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            services.Service.GetStaffAsync(Guid.NewGuid(), "", CancellationToken.None));
    }

    [Fact]
    public async Task PublishedCancellationRemainsAvailableAsCalendarUpdate()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await PublishAsync(services.Service, id, SessionVisibility.Public);
        await services.Service.ExecuteAsync(new CancelSessionCommand(id, 3, "Venue unavailable", "cancel", Now), "officer-2", CancellationToken.None);

        var publicSession = await services.Service.GetPublicAsync(id, CancellationToken.None);
        var calendar = await services.Service.GetIcsAsync(id, Now, CancellationToken.None);

        Assert.Equal("Cancelled", publicSession!.Status);
        Assert.NotNull(calendar);
        Assert.Contains("STATUS:CANCELLED", calendar, StringComparison.Ordinal);
        Assert.Contains("SEQUENCE:1", calendar, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationReasonIsRestrictedToStaffViews()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        const string reason = "Private coordination note";
        await PublishAsync(services.Service, id, SessionVisibility.Public);
        await services.Service.ExecuteAsync(new CancelSessionCommand(id, 3, reason, "cancel", Now), "officer-2", CancellationToken.None);

        Assert.Equal(reason, (await services.Service.GetStaffAsync(id, "officer-1", CancellationToken.None))!.CancellationReason);
        Assert.Null((await services.Service.GetPublicAsync(id, CancellationToken.None))!.CancellationReason);
        Assert.Null(Assert.Single(await services.Service.ListPublicAsync(null, CancellationToken.None)).CancellationReason);
        Assert.Null(Assert.Single((await services.Service.QueryPublicAsync(new PublicSessionQuery(), CancellationToken.None)).Items).CancellationReason);
        Assert.Null((await services.Service.GetPublicAsync(id, CancellationToken.None))!.ChannelLink);
        Assert.Null(Assert.Single(await services.Service.ListPublicAsync(null, CancellationToken.None)).ChannelLink);
        Assert.Null(Assert.Single((await services.Service.QueryPublicAsync(new PublicSessionQuery(), CancellationToken.None)).Items).ChannelLink);
        Assert.NotNull((await services.Service.GetStaffAsync(id, "officer-1", CancellationToken.None))!.ChannelLink);
        var calendar = (await services.Service.GetIcsAsync(id, Now, CancellationToken.None))!;
        var invitation = (await services.Service.GetInvitationTextAsync(id, CancellationToken.None))!;
        Assert.DoesNotContain(reason, calendar, StringComparison.Ordinal);
        Assert.DoesNotContain("URL:", calendar, StringComparison.Ordinal);
        Assert.DoesNotContain(reason, invitation, StringComparison.Ordinal);
        Assert.DoesNotContain("Join:", invitation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_a_reviewed_unpublished_session_never_exposes_it_publicly()
    {
        var services = CreateServices();
        var id = Guid.NewGuid();
        await services.Service.ExecuteAsync(CreateDraft(id), "officer-1", CancellationToken.None);
        await services.Service.ExecuteAsync(new ReviewSessionCommand(id, 1, false, "review", Now), "officer-2", CancellationToken.None);
        await services.Service.ExecuteAsync(new CancelSessionCommand(id, 2, "cancelled", "cancel", Now), "officer-2", CancellationToken.None);

        Assert.Null(await services.Service.GetPublicAsync(id, CancellationToken.None));
        Assert.Null(await services.Service.GetIcsAsync(id, Now, CancellationToken.None));
        Assert.Null(await services.Service.GetInvitationTextAsync(id, CancellationToken.None));
        Assert.DoesNotContain(await services.Service.ListPublicAsync(null, CancellationToken.None), item => item.SessionId == id);
        Assert.DoesNotContain((await services.Service.QueryPublicAsync(new PublicSessionQuery(), CancellationToken.None)).Items,
            item => item.SessionId == id);
    }

    [Fact]
    public async Task Local_date_query_can_continue_after_bounded_scan_with_no_matches()
    {
        var services = CreateServices();
        for (var index = 0; index < 1001; index++)
        {
            var id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}");
            await PublishAsync(services.Service, id, SessionVisibility.Public);
        }
        var query = new PublicSessionQuery(LocalDateFrom: new DateOnly(2026, 11, 1), LocalDateTo: new DateOnly(2026, 11, 1));
        var first = await services.Service.QueryPublicAsync(query, CancellationToken.None);
        Assert.Empty(first.Items);
        Assert.NotNull(first.Next);
        var second = await services.Service.QueryPublicAsync(query with
        {
            AfterStartUtc = first.Next.StartUtc,
            AfterSessionId = first.Next.SessionId
        }, CancellationToken.None);
        Assert.Empty(second.Items);
        Assert.Null(second.Next);
    }

    [Fact]
    public async Task Public_query_uses_each_session_local_date_before_paging()
    {
        var services = CreateServices();
        var localPreviousDay = Guid.NewGuid();
        var localRequestedDay = Guid.NewGuid();
        foreach (var (id, start) in new[]
        {
            (localPreviousDay, new DateTimeOffset(2026, 11, 1, 3, 30, 0, TimeSpan.Zero)),
            (localRequestedDay, new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero))
        })
        {
            await services.Service.ExecuteAsync(CreateDraft(id) with { StartUtc = start, TimeZoneId = "America/New_York" }, "officer-1", CancellationToken.None);
            await services.Service.ExecuteAsync(new ReviewSessionCommand(id, 1, false, "review", Now), "officer-2", CancellationToken.None);
            await services.Service.ExecuteAsync(new ScheduleSessionCommand(id, 2, "publish", Now), "officer-2", CancellationToken.None);
        }

        var page = await services.Service.QueryPublicAsync(new PublicSessionQuery(
            LocalDateFrom: new DateOnly(2026, 11, 1), LocalDateTo: new DateOnly(2026, 11, 1), Limit: 1), CancellationToken.None);
        Assert.Equal(localRequestedDay, Assert.Single(page.Items).SessionId);
        Assert.Null(page.Next);
        await Assert.ThrowsAsync<ArgumentException>(() => services.Service.QueryPublicAsync(new PublicSessionQuery(
            LocalDateFrom: new DateOnly(2026, 11, 2), LocalDateTo: new DateOnly(2026, 11, 1)), CancellationToken.None));
    }

    [Fact]
    public async Task Public_query_pages_only_published_public_sessions_and_validates_positions()
    {
        var services = CreateServices();
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await PublishAsync(services.Service, firstId, SessionVisibility.Public);
        await PublishAsync(services.Service, secondId, SessionVisibility.Public);
        await PublishAsync(services.Service, Guid.NewGuid(), SessionVisibility.Internal);
        await services.Service.ExecuteAsync(CreateDraft(Guid.NewGuid()), "officer-1", CancellationToken.None);

        var first = await services.Service.QueryPublicAsync(new PublicSessionQuery(Limit: 1), CancellationToken.None);
        Assert.Equal(firstId, Assert.Single(first.Items).SessionId);
        Assert.NotNull(first.Next);
        var second = await services.Service.QueryPublicAsync(new PublicSessionQuery(
            AfterStartUtc: first.Next.StartUtc, AfterSessionId: first.Next.SessionId, Limit: 1), CancellationToken.None);
        Assert.Equal(secondId, Assert.Single(second.Items).SessionId);
        Assert.Null(second.Next);

        await Assert.ThrowsAsync<ArgumentException>(() => services.Service.QueryPublicAsync(
            new PublicSessionQuery(AfterSessionId: firstId), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => services.Service.QueryPublicAsync(
            new PublicSessionQuery(Limit: 101), CancellationToken.None));
    }

    private static (CommunitySessionService Service, FakeSessionStore Store) CreateServices()
    {
        var store = new FakeSessionStore();
        return (new CommunitySessionService(store, new FixtureMaterialEligibility()), store);
    }

    private sealed class FixtureMaterialEligibility : ISessionMaterialEligibility
    {
        public bool Eligible { get; set; } = true;
        public SessionMaterialEligibilityRequest? LastRequest { get; private set; }

        public Task<bool> AreEligibleAsync(SessionMaterialEligibilityRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(Eligible);
        }
    }

    private static async Task PublishAsync(ICommunitySessionService service, Guid sessionId, SessionVisibility visibility)
    {
        await service.ExecuteAsync(CreateDraft(sessionId) with { Visibility = visibility }, "officer-1", CancellationToken.None);
        await service.ExecuteAsync(new ReviewSessionCommand(sessionId, 1, false, "reviewed", Now), "officer-2", CancellationToken.None);
        await service.ExecuteAsync(new ScheduleSessionCommand(sessionId, 2, "publish", Now), "officer-2", CancellationToken.None);
    }

    private static CreateSessionDraftCommand CreateDraft(Guid sessionId) =>
        new(
            sessionId,
            "Malaria prevention talk",
            "An evening talk on bed nets and stagnant water.",
            "Adults in the Budiriro catchment",
            ["malaria"],
            "ZW",
            "harare",
            "HumanHealth",
            "Nurse Moyo",
            "en",
            Start,
            HarareZoneId(),
            60,
            50,
            [new SessionMaterialRef("card-1", "v3")],
            SessionVisibility.Public,
            SessionChannelType.WhatsAppGroup,
            "https://chat.example/join/abc",
            [24, 1],
            "test",
            Now);

    private static UpdateSessionCommand UpdateDraft(Guid sessionId, long expectedVersion) =>
        new(
            sessionId,
            expectedVersion,
            "Malaria prevention talk (revised)",
            "An evening talk on bed nets and stagnant water.",
            "Adults in the Budiriro catchment",
            ["malaria"],
            "Nurse Moyo",
            "en",
            Start.AddDays(1),
            60,
            50,
            [new SessionMaterialRef("card-1", "v3")],
            SessionVisibility.Public,
            SessionChannelType.WhatsAppGroup,
            "https://chat.example/join/abc",
            [24, 1],
            "test update",
            Now);

    private sealed record BogusCommand(Guid SessionId, long ExpectedVersion, string Reason, DateTimeOffset RecordedAtUtc)
        : SessionCommand(SessionId, ExpectedVersion, Reason, RecordedAtUtc);

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

    private sealed class FakeSessionStore : ICommunitySessionStore
    {
        private readonly Dictionary<Guid, DomainSessions.CommunitySession> _sessions = new();
        private readonly Dictionary<Guid, long> _versions = new();

        public SessionReminderSync LastSync { get; private set; } = SessionReminderSync.None;
        public IReadOnlyList<DomainSessions.SessionReminderOccurrence> LastOccurrences { get; private set; } = [];
        public int Revisions { get; private set; }

        public Task<DomainSessions.CommunitySession?> LoadAsync(Guid sessionId, CancellationToken ct) =>
            Task.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : null);

        public Task InsertAsync(DomainSessions.CommunitySession session, string actorId, string reason, DateTimeOffset recordedAtUtc, CancellationToken ct)
        {
            _sessions.Add(session.SessionId, session);
            _versions[session.SessionId] = session.Version;
            Revisions++;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(
            DomainSessions.CommunitySession session,
            long expectedVersion,
            string actorId,
            string reason,
            DateTimeOffset recordedAtUtc,
            SessionReminderSync reminderSync,
            IReadOnlyList<DomainSessions.SessionReminderOccurrence>? occurrences,
            CancellationToken ct)
        {
            if (!_versions.TryGetValue(session.SessionId, out var current) || current != expectedVersion)
            {
                throw new SessionVersionConflictException(session.SessionId, expectedVersion, current);
            }

            _versions[session.SessionId] = session.Version;
            LastSync = reminderSync;
            LastOccurrences = occurrences ?? [];
            Revisions++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DomainSessions.CommunitySession>> ListPublicAsync(int take, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DomainSessions.CommunitySession>>(_sessions.Values.Take(take).ToList());

        public Task<IReadOnlyList<DomainSessions.CommunitySession>> QueryPublicAsync(PublicSessionQuery query, int take, CancellationToken ct)
        {
            var results = _sessions.Values.Where(item => item.Visibility == DomainSessions.SessionVisibility.Public &&
                (item.Status == DomainSessions.CommunitySessionStatus.Scheduled || item.Status == DomainSessions.CommunitySessionStatus.Cancelled || item.Status == DomainSessions.CommunitySessionStatus.Completed));
            if (query.Status is { } status)
                results = results.Where(item => item.Status.ToString() == status);
            if (query.AfterStartUtc is { } after && query.AfterSessionId is { } afterId)
                results = results.Where(item => item.StartUtc > after || item.StartUtc == after && item.SessionId.CompareTo(afterId) > 0);
            return Task.FromResult<IReadOnlyList<DomainSessions.CommunitySession>>(results
                .OrderBy(item => item.StartUtc).ThenBy(item => item.SessionId).Take(take).ToList());
        }

        public Task<int> CountRevisionsAsync(Guid sessionId, CancellationToken ct) => Task.FromResult(Revisions);

        public Task<int> CountPendingRemindersAsync(Guid sessionId, CancellationToken ct) => Task.FromResult(0);

        public Task<int> CountSuppressedRemindersAsync(Guid sessionId, CancellationToken ct) => Task.FromResult(0);
    }
}
