using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Sessions;
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

            await service.ExecuteAsync(CreateDraft(otherId, "officer-1"), "officer-1", CancellationToken.None);
            var otherReviewed = await service.ExecuteAsync(
                new ReviewSessionCommand(otherId, 1, false, "peer review", DateTimeOffset.UtcNow),
                "officer-2",
                CancellationToken.None);
            Assert.Equal(2, otherReviewed.Version);
        }
        finally
        {
            await CleanupAsync(selfId, otherId);
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

            var update = UpdateDraft(sessionId, 3, "Malaria prevention talk (revised)");
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

            await service.ExecuteAsync(UpdateDraft(sessionId, 3, "Malaria prevention talk (revised)"), "officer-2", CancellationToken.None);

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
        var sessions = new CommunitySessionService(sessionStore);
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

            var changed = await service.SubscribeAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "MobilePush", CancellationToken.None);
            Assert.Equal(2, changed.Version);
            var actor = await db.Actors.SingleAsync(item => item.ActorId == accountId);
            actor.Status = SwiftAid.Infrastructure.Persistence.PersistentActorStatus.Suspended;
            await db.SaveChangesAsync();
            Assert.False(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "MobilePush", CancellationToken.None));

            actor.Status = SwiftAid.Infrastructure.Persistence.PersistentActorStatus.Active;
            await db.SaveChangesAsync();
            Assert.True(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "MobilePush", CancellationToken.None));

            var unsubscribed = await service.UnsubscribeAsync(sessionId, accountId, changed.Version, CancellationToken.None);
            Assert.Equal(EventSubscriptionStatus.Unsubscribed, unsubscribed.Status);
            Assert.Equal(3, unsubscribed.Version);
            Assert.False(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "MobilePush", CancellationToken.None));
            var renewed = await service.SubscribeAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "MobilePush", CancellationToken.None);
            Assert.Equal(4, renewed.Version);
            Assert.False(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "MobilePush", CancellationToken.None, changed.Version));
            Assert.True(await service.CanDispatchReminderAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "MobilePush", CancellationToken.None, renewed.Version));
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
        var sessions = new CommunitySessionService(sessionStore);
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
    public async Task Due_session_reminder_materializes_once_and_cancellation_invalidates_claim()
    {
        var sessionId = Guid.NewGuid();
        var accountId = $"session-reminder-{Guid.NewGuid():N}";
        var secondAccountId = $"session-reminder-{Guid.NewGuid():N}";
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var sessionStore = new PostgresCommunitySessionStore(db);
        var subscriptionService = new EventSubscriptionService(
            new PostgresEventSubscriptionStore(db), new CommunitySessionService(sessionStore), TimeProvider.System);
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
                Assert.Equal(options.ConsentReference, delivery.ConsentReference);
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
                new PostgresEventSubscriptionStore(db), new CommunitySessionService(sessionStore), TimeProvider.System);
            var options = Assert.IsType<EventSubscriptionOptions>(
                await service.GetOptionsAsync(sessionId, accountId, CancellationToken.None));
            await service.SubscribeAsync(sessionId, accountId, options.SessionVersion,
                options.ConsentReference, "Email", CancellationToken.None);
            await db.SessionReminderOccurrences.Where(item => item.SessionId == sessionId).Take(1)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    item => item.RemindAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));

            var deliveryStore = new PostgresSessionReminderDeliveryStore(db);
            var now = DateTimeOffset.UtcNow;
            Assert.Equal(1, await deliveryStore.MaterializeDueAsync(now, 10, CancellationToken.None));
            var lease = Assert.Single(await deliveryStore.ClaimDueAsync(
                now, TimeSpan.FromMinutes(1), 10, ["Email"], CancellationToken.None));

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
        return new TestCommunitySessionService(new CommunitySessionService(store), store);
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
