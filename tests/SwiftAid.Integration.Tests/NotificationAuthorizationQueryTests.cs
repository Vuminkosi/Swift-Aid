using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Notifications;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Domain.Notifications;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Cases;
using SwiftAid.Infrastructure.Persistence.Notifications;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

[Collection(CaseAuthorityPostgresCollection.Name)]
public sealed class NotificationAuthorizationQueryTests
{
    [CaseAuthorityPostgresFact]
    public async Task Sql_page_prefilter_matches_shared_authorizer_and_excludes_unsupported_recipient_requirement()
    {
        await using var fixture = await NotificationPostgresFixture.CreateAsync().ConfigureAwait(false);
        var currentOwner = await fixture.AddNotificationAsync(
            fixture.Authority.CaseId,
            NotificationRecipientRequirement.CurrentAccountableOwner).ConfigureAwait(false);
        await fixture.AddNotificationAsync(
            fixture.Authority.SiblingCaseId,
            NotificationRecipientRequirement.AuthorizedCaseMember).ConfigureAwait(false);

        var authorized = await fixture.AuthorizeAsync(
            fixture.Authority.CaseId,
            NotificationRecipientRequirement.CurrentAccountableOwner).ConfigureAwait(false);
        var page = await fixture.ReadPageAsync().ConfigureAwait(false);

        Assert.True(authorized);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.UnreadCount);
        var item = Assert.Single(page.Items);
        Assert.Equal(currentOwner, item.Id);
    }

    [CaseAuthorityPostgresFact]
    public async Task Sql_prefilter_matches_current_policy_after_actor_epoch_owner_lifecycle_and_grant_changes()
    {
        await using var fixture = await NotificationPostgresFixture.CreateAsync().ConfigureAwait(false);
        await fixture.AddNotificationAsync(
            fixture.Authority.CaseId,
            NotificationRecipientRequirement.CurrentAccountableOwner).ConfigureAwait(false);

        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);

        await fixture.SetActorStatusAsync(PersistentActorStatus.Suspended).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetActorStatusAsync(PersistentActorStatus.Active).ConfigureAwait(false);

        await fixture.SetActorExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1)).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetActorExpiryAsync(null).ConfigureAwait(false);

        await fixture.SetAccessEpochAsync(fixture.Authority.AccessEpoch + 1).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync(actorEpoch: fixture.Authority.AccessEpoch).ConfigureAwait(false);
        await fixture.SetAccessEpochAsync(fixture.Authority.AccessEpoch).ConfigureAwait(false);

        await fixture.Authority.SetCaseOwnerAsync(fixture.Authority.NewOwnerActorId).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.Authority.SetCaseOwnerAsync(fixture.Authority.ActorId).ConfigureAwait(false);

        await fixture.SetCaseWorkflowAsync(CaseWorkflow.Resolved).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetCaseWorkflowAsync(CaseWorkflow.Reported).ConfigureAwait(false);

        await fixture.RemoveCaseRegionsAsync().ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.RestoreCaseRegionAsync().ConfigureAwait(false);

        await fixture.Authority.SetGrantWorkspaceAsync(fixture.Authority.AlternateWorkspaceId).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.Authority.SetGrantWorkspaceAsync(fixture.Authority.WorkspaceId).ConfigureAwait(false);

        await fixture.SetOrganizationStatusAsync(PersistentResourceStatus.Suspended).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetOrganizationStatusAsync(PersistentResourceStatus.Active).ConfigureAwait(false);

        await fixture.SetWorkspaceStatusAsync(PersistentResourceStatus.Suspended).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetWorkspaceStatusAsync(PersistentResourceStatus.Active).ConfigureAwait(false);

        await fixture.SetMembershipExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1)).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetMembershipExpiryAsync(DateTimeOffset.UtcNow.AddDays(2)).ConfigureAwait(false);

        await fixture.SetMembershipStatusAsync(PersistentMembershipStatus.Suspended).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetMembershipStatusAsync(PersistentMembershipStatus.Active).ConfigureAwait(false);

        await fixture.SetMembershipValidFromAsync(DateTimeOffset.UtcNow.AddMinutes(1)).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetMembershipValidFromAsync(DateTimeOffset.UtcNow.AddDays(-1)).ConfigureAwait(false);

        await fixture.SetGrantStatusAsync(PersistentGrantStatus.Suspended).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetGrantStatusAsync(PersistentGrantStatus.Active).ConfigureAwait(false);

        await fixture.SetGrantValidFromAsync(DateTimeOffset.UtcNow.AddMinutes(1)).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetGrantValidFromAsync(DateTimeOffset.UtcNow.AddDays(-1)).ConfigureAwait(false);

        await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1)).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddDays(2)).ConfigureAwait(false);

        await fixture.SetGrantSensitivityAsync("sens-other").ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetGrantSensitivityAsync("sens-A").ConfigureAwait(false);

        await fixture.SetGrantCountryAsync("ZA").ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetGrantCountryAsync("SZ").ConfigureAwait(false);

        await fixture.SetGrantSectorAsync(Sector.AnimalHealth).ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.SetGrantSectorAsync(Sector.HumanHealth).ConfigureAwait(false);

        await fixture.RemoveGrantRegionAsync().ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
        await fixture.RestoreGrantRegionAsync().ConfigureAwait(false);

        await fixture.RemoveReadCaseOperationAsync().ConfigureAwait(false);
        await fixture.AssertQueryMatchesSharedPolicyAsync().ConfigureAwait(false);
    }

    [CaseAuthorityPostgresFact]
    public async Task Shared_authorizer_denies_a_missing_case_resource()
    {
        await using var fixture = await NotificationPostgresFixture.CreateAsync().ConfigureAwait(false);

        var authorized = await fixture.AuthorizeAsync(
            Guid.NewGuid(),
            NotificationRecipientRequirement.CurrentAccountableOwner).ConfigureAwait(false);

        Assert.False(authorized);
    }

    [CaseAuthorityPostgresFact]
    public async Task Page_rechecks_candidate_when_authority_changes_after_sql_page_query()
    {
        await using var fixture = await NotificationPostgresFixture.CreateAsync(changeOwnerAfterPageRead: true)
            .ConfigureAwait(false);
        await fixture.AddNotificationAsync(
            fixture.Authority.CaseId,
            NotificationRecipientRequirement.CurrentAccountableOwner).ConfigureAwait(false);

        var page = await fixture.ReadPageAsync().ConfigureAwait(false);

        Assert.True(fixture.MutationApplied);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.UnreadCount);
        Assert.Empty(page.Items);
    }

    [CaseAuthorityPostgresFact]
    public async Task Follow_up_reminders_are_idempotent_and_stale_occurrences_are_suppressed()
    {
        await using var fixture = await NotificationPostgresFixture.CreateAsync().ConfigureAwait(false);
        var (followUpId, _) = await fixture.AddDueFollowUpAsync().ConfigureAwait(false);

        Assert.Equal(1, await fixture.DispatchDueFollowUpsAsync().ConfigureAwait(false));
        Assert.Equal(1, await fixture.DispatchDueFollowUpsAsync().ConfigureAwait(false));
        Assert.Equal(1, await fixture.CountFollowUpNotificationsAsync(followUpId).ConfigureAwait(false));
        Assert.Equal(1, (await fixture.ReadPageAsync().ConfigureAwait(false)).TotalCount);

        await fixture.RescheduleFollowUpAsync(followUpId, 10002).ConfigureAwait(false);
        Assert.Equal(0, (await fixture.ReadPageAsync().ConfigureAwait(false)).TotalCount);

        Assert.Equal(1, await fixture.DispatchDueFollowUpsAsync().ConfigureAwait(false));
        Assert.Equal(2, await fixture.CountFollowUpNotificationsAsync(followUpId).ConfigureAwait(false));
        Assert.Equal(1, (await fixture.ReadPageAsync().ConfigureAwait(false)).TotalCount);

        await fixture.CompleteFollowUpAsync(followUpId).ConfigureAwait(false);
        Assert.Equal(0, (await fixture.ReadPageAsync().ConfigureAwait(false)).TotalCount);
    }

    [CaseAuthorityPostgresFact]
    public async Task Reading_or_dismissing_a_reminder_does_not_complete_the_follow_up()
    {
        await using var fixture = await NotificationPostgresFixture.CreateAsync().ConfigureAwait(false);
        var (followUpId, _) = await fixture.AddDueFollowUpAsync().ConfigureAwait(false);

        Assert.Equal(1, await fixture.DispatchDueFollowUpsAsync().ConfigureAwait(false));
        var notificationId = await fixture.GetFollowUpNotificationIdAsync(followUpId).ConfigureAwait(false);

        Assert.True(await fixture.MarkReadAsync(notificationId).ConfigureAwait(false));
        Assert.Equal(FollowUpStatus.Scheduled, await fixture.GetFollowUpStatusAsync(followUpId).ConfigureAwait(false));
        var readPage = await fixture.ReadPageAsync().ConfigureAwait(false);
        Assert.Equal(1, readPage.TotalCount);
        Assert.Equal(0, readPage.UnreadCount);

        Assert.True(await fixture.DismissAsync(notificationId).ConfigureAwait(false));
        Assert.Equal(FollowUpStatus.Scheduled, await fixture.GetFollowUpStatusAsync(followUpId).ConfigureAwait(false));
        Assert.Equal(0, (await fixture.ReadPageAsync().ConfigureAwait(false)).TotalCount);
        Assert.Equal(1, await fixture.CountFollowUpNotificationsAsync(followUpId).ConfigureAwait(false));
    }

    [CaseAuthorityPostgresFact]
    public async Task Follow_up_reminder_text_is_generic_and_contains_no_case_or_identity_details()
    {
        await using var fixture = await NotificationPostgresFixture.CreateAsync().ConfigureAwait(false);
        var (followUpId, _) = await fixture.AddDueFollowUpAsync().ConfigureAwait(false);

        Assert.Equal(1, await fixture.DispatchDueFollowUpsAsync().ConfigureAwait(false));
        var notification = Assert.Single((await fixture.ReadPageAsync().ConfigureAwait(false)).Items);

        Assert.Equal("A case needs your attention.", notification.DisplayText);
        Assert.DoesNotContain(followUpId.ToString("D"), notification.DisplayText, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Authority.ActorId, notification.DisplayText, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic follow-up", notification.DisplayText, StringComparison.Ordinal);
    }
}

/// <summary>
/// Uses the repository's disposable SQLAUTH PostgreSQL target. Every seeded identifier is unique
/// per test; cleanup removes only rows created by this fixture before case authority rows.
/// </summary>
internal sealed class NotificationPostgresFixture : IAsyncDisposable
{
    private readonly List<Guid> _eventIds = [];
    private readonly List<Guid> _payloadIds = [];
    private readonly List<Guid> _receiptIds = [];
    private readonly List<Guid> _notificationIds = [];
    private readonly List<Guid> _followUpIds = [];
    private readonly ChangeOwnerAfterNotificationPageRead? _queryInterceptor;

    private NotificationPostgresFixture(
        CaseAuthorityPostgresFixture authority,
        bool changeOwnerAfterPageRead)
    {
        Authority = authority;
        _queryInterceptor = changeOwnerAfterPageRead
            ? new ChangeOwnerAfterNotificationPageRead(
                authority.ConnectionString,
                authority.CaseId,
                authority.NewOwnerActorId)
            : null;
    }

    public CaseAuthorityPostgresFixture Authority { get; }

    public bool MutationApplied => _queryInterceptor?.MutationApplied == true;

    public static async Task<NotificationPostgresFixture> CreateAsync(
        bool changeOwnerAfterPageRead = false)
    {
        var authority = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        return new NotificationPostgresFixture(authority, changeOwnerAfterPageRead);
    }

    public async Task<Guid> AddNotificationAsync(
        Guid caseId,
        NotificationRecipientRequirement requirement)
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var payloadId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        var notificationId = Guid.Empty;
        _eventIds.Add(eventId);
        _payloadIds.Add(payloadId);
        _receiptIds.Add(receiptId);

        await using (var dbContext = Authority.CreateDbContext())
        {
            dbContext.CommandReceipts.Add(new CommandReceiptEntity
            {
                ReceiptId = receiptId,
                ActorId = Authority.ActorId,
                CommandId = commandId,
                CaseId = caseId,
                CommandType = "CaseAssignment",
                SchemaVersion = 1,
                CanonicalPayloadHash = new byte[32],
                FirstAggregateVersion = 1,
                LastAggregateVersion = 1,
                ReportRevision = 1,
                AcceptedAtUtc = now
            });
            dbContext.ProtectedEventPayloads.Add(new ProtectedEventPayloadEntity
            {
                PayloadId = payloadId,
                ProtectedBytes = [0x01],
                ProtectionPurposeVersion = "notification-query-test-v1",
                CreatedAtUtc = now
            });
            dbContext.CaseEvents.Add(new CaseEventEntity
            {
                EventId = eventId,
                CaseId = caseId,
                AggregateVersion = 1,
                ReportRevision = 1,
                CommandId = commandId,
                ActorId = Authority.ActorId,
                EventType = "CaseAssigned",
                SchemaVersion = 1,
                RecordedAtUtc = now,
                PayloadId = payloadId
            });
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        await using (var dbContext = Authority.CreateDbContext())
        {
            var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
            var policy = new CaseAccessPolicy(
                new PostgresCurrentAccessGrantStore(dbContext, unitOfWork),
                new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork));
            var authorizer = new NotificationResourceAuthorizer(dbContext, policy, unitOfWork);
            var writer = new PostgresNotificationStore(dbContext, authorizer);
            var item = await writer.CreateWithDeliveryOutboxIfMissingAsync(
                    eventId,
                    $"notification-query-test-{Guid.NewGuid():N}",
                    Authority.ActorId,
                    NotificationKind.CaseNeedsAttention,
                    NotificationResourceKind.ManagedCase,
                    caseId,
                    NotificationAction.OpenCase,
                    requirement,
                    now,
                    CancellationToken.None)
                .ConfigureAwait(false);
            notificationId = item.NotificationId;
            _notificationIds.Add(notificationId);
        }

        return notificationId;
    }

    public async Task<(Guid FollowUpId, Guid ScheduleEventId)> AddDueFollowUpAsync()
    {
        var followUpId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var payloadId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _followUpIds.Add(followUpId);
        _eventIds.Add(eventId);
        _payloadIds.Add(payloadId);
        _receiptIds.Add(receiptId);
        await using var db = Authority.CreateDbContext();
        db.CommandReceipts.Add(new CommandReceiptEntity
        {
            ReceiptId = receiptId, ActorId = Authority.ActorId, CommandId = commandId,
            CaseId = Authority.CaseId, CommandType = "CaseFollowUp", SchemaVersion = 1,
            CanonicalPayloadHash = new byte[32], FirstAggregateVersion = 10001, LastAggregateVersion = 10001,
            ReportRevision = 1, AcceptedAtUtc = now
        });
        db.ProtectedEventPayloads.Add(new ProtectedEventPayloadEntity
        {
            PayloadId = payloadId, ProtectedBytes = [0x01],
            ProtectionPurposeVersion = "follow-up-reminder-test-v1", CreatedAtUtc = now
        });
        db.CaseEvents.Add(new CaseEventEntity
        {
            EventId = eventId, CaseId = Authority.CaseId, AggregateVersion = 10001,
            ReportRevision = 1, CommandId = commandId, ActorId = Authority.ActorId,
            EventType = "CaseFollowUpScheduled", SchemaVersion = 1, RecordedAtUtc = now, PayloadId = payloadId
        });
        db.ManagedCaseFollowUps.Add(new ManagedCaseFollowUpEntity
        {
            FollowUpId = followUpId, CaseId = Authority.CaseId, Purpose = "synthetic follow-up",
            AssigneeActorId = Authority.ActorId, DueAtUtc = now.AddMinutes(-1), Status = FollowUpStatus.Scheduled,
            ScheduledVersion = 10001, CurrentScheduleVersion = 10001, CurrentScheduleEventId = eventId
        });
        db.AccessGrantOperations.Add(new AccessGrantOperationEntity
        {
            GrantGroupId = Authority.GrantGroupId, Operation = CaseOperation.ManageFollowUp
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        return (followUpId, eventId);
    }

    public async Task<int> DispatchDueFollowUpsAsync()
    {
        await using var db = Authority.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(db);
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(db, unitOfWork),
            new PostgresCaseAccessDescriptorStore(db, unitOfWork));
        var writer = new PostgresNotificationStore(db, new NotificationResourceAuthorizer(db, policy, unitOfWork));
        var dispatcher = new FollowUpReminderDispatcher(
            new PostgresFollowUpReminderScheduleStore(db), writer);
        return await dispatcher.DispatchAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<Guid> RescheduleFollowUpAsync(Guid followUpId, long version)
    {
        var eventId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var payloadId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _eventIds.Add(eventId);
        _payloadIds.Add(payloadId);
        _receiptIds.Add(receiptId);
        await using var db = Authority.CreateDbContext();
        db.CommandReceipts.Add(new CommandReceiptEntity
        {
            ReceiptId = receiptId, ActorId = Authority.ActorId, CommandId = commandId,
            CaseId = Authority.CaseId, CommandType = "CaseFollowUpOutcome", SchemaVersion = 1,
            CanonicalPayloadHash = new byte[32], FirstAggregateVersion = version, LastAggregateVersion = version,
            ReportRevision = 1, AcceptedAtUtc = now
        });
        db.ProtectedEventPayloads.Add(new ProtectedEventPayloadEntity
        {
            PayloadId = payloadId, ProtectedBytes = [0x01],
            ProtectionPurposeVersion = "follow-up-reminder-test-v1", CreatedAtUtc = now
        });
        db.CaseEvents.Add(new CaseEventEntity
        {
            EventId = eventId, CaseId = Authority.CaseId, AggregateVersion = version,
            ReportRevision = 1, CommandId = commandId, ActorId = Authority.ActorId,
            EventType = "CaseFollowUpOutcomeRecorded", SchemaVersion = 1, RecordedAtUtc = now, PayloadId = payloadId
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        await db.ManagedCaseFollowUps.Where(item => item.FollowUpId == followUpId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.CurrentScheduleVersion, version)
                .SetProperty(item => item.CurrentScheduleEventId, eventId)
                .SetProperty(item => item.OutcomeVersion, (long?)version)
                .SetProperty(item => item.DueAtUtc, now.AddMinutes(-1)))
            .ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return eventId;
    }

    public async Task CompleteFollowUpAsync(Guid followUpId)
    {
        await using var db = Authority.CreateDbContext();
        await db.ManagedCaseFollowUps.Where(item => item.FollowUpId == followUpId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, FollowUpStatus.Completed))
            .ConfigureAwait(false);
    }

    public async Task<Guid> GetFollowUpNotificationIdAsync(Guid followUpId)
    {
        await using var db = Authority.CreateDbContext();
        return await db.NotificationItems.AsNoTracking()
            .Where(item => item.RelatedResourceId == followUpId)
            .Select(item => item.NotificationId)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    public async Task<FollowUpStatus> GetFollowUpStatusAsync(Guid followUpId)
    {
        await using var db = Authority.CreateDbContext();
        return await db.ManagedCaseFollowUps.AsNoTracking()
            .Where(item => item.FollowUpId == followUpId)
            .Select(item => item.Status)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    public async Task<bool> MarkReadAsync(Guid notificationId) =>
        await MutateNotificationAsync(notificationId, dismiss: false).ConfigureAwait(false);

    public async Task<bool> DismissAsync(Guid notificationId) =>
        await MutateNotificationAsync(notificationId, dismiss: true).ConfigureAwait(false);

    private async Task<bool> MutateNotificationAsync(Guid notificationId, bool dismiss)
    {
        await using var db = Authority.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(db);
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(db, unitOfWork),
            new PostgresCaseAccessDescriptorStore(db, unitOfWork));
        var store = new PostgresNotificationStore(db, new NotificationResourceAuthorizer(db, policy, unitOfWork));
        return dismiss
            ? await store.DismissAsync(Authority.CreateActor(), notificationId, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false)
            : await store.MarkReadAsync(Authority.CreateActor(), notificationId, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
    }

    public async Task<int> CountFollowUpNotificationsAsync(Guid followUpId)
    {
        await using var db = Authority.CreateDbContext();
        return await db.NotificationItems.CountAsync(item => item.RelatedResourceId == followUpId)
            .ConfigureAwait(false);
    }

    public async Task<bool> AuthorizeAsync(
        Guid caseId,
        NotificationRecipientRequirement requirement)
    {
        await using var dbContext = Authority.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(dbContext, unitOfWork),
            new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork));
        var authorizer = new NotificationResourceAuthorizer(dbContext, policy, unitOfWork);
        return await authorizer.IsCurrentlyAuthorizedAsync(
                Authority.CreateActor(),
                NotificationResourceKind.ManagedCase,
                caseId,
                NotificationAction.OpenCase,
                requirement,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    public async Task AssertQueryMatchesSharedPolicyAsync(long? actorEpoch = null)
    {
        var authorized = await AuthorizeAsync(
            Authority.CaseId,
            NotificationRecipientRequirement.CurrentAccountableOwner).ConfigureAwait(false);
        var page = await ReadPageAsync(actorEpoch).ConfigureAwait(false);

        Assert.Equal(authorized ? 1 : 0, page.TotalCount);
        Assert.Equal(authorized ? 1 : 0, page.UnreadCount);
        Assert.Equal(authorized ? 1 : 0, page.Items.Count);
    }

    public async Task<NotificationPage> ReadPageAsync(long? actorEpoch = null)
    {
        await using var dbContext = Authority.CreateDbContext(_queryInterceptor);
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(dbContext, unitOfWork),
            new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork));
        var authorizer = new NotificationResourceAuthorizer(dbContext, policy, unitOfWork);
        var store = new PostgresNotificationStore(dbContext, authorizer);
        return await store.GetPageAsync(
                Authority.CreateActor(actorEpoch),
                page: 1,
                pageSize: 20,
                includeDismissed: false,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    public async Task SetActorStatusAsync(PersistentActorStatus status)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.Actors.Where(actor => actor.ActorId == Authority.ActorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(actor => actor.Status, status))
            .ConfigureAwait(false);
    }

    public async Task SetActorExpiryAsync(DateTimeOffset? expiry)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.Actors.Where(actor => actor.ActorId == Authority.ActorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(actor => actor.ExpiresAtUtc, expiry))
            .ConfigureAwait(false);
    }

    public async Task SetAccessEpochAsync(long epoch)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.ActorAccessStates.Where(state => state.ActorId == Authority.ActorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(state => state.AccessEpoch, epoch))
            .ConfigureAwait(false);
    }

    public async Task SetCaseWorkflowAsync(CaseWorkflow workflow)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.ManagedCases.Where(caseRow => caseRow.CaseId == Authority.CaseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(caseRow => caseRow.Workflow, workflow))
            .ConfigureAwait(false);
    }

    public async Task RemoveCaseRegionsAsync()
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.ManagedCaseRegions.Where(region => region.CaseId == Authority.CaseId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
    }

    public async Task RestoreCaseRegionAsync()
    {
        await using var dbContext = Authority.CreateDbContext();
        dbContext.ManagedCaseRegions.Add(new ManagedCaseRegionEntity
        {
            CaseId = Authority.CaseId,
            RegionId = Authority.CaseRegionId
        });
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task RemoveReadCaseOperationAsync()
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.AccessGrantOperations
            .Where(operation => operation.GrantGroupId == Authority.GrantGroupId &&
                                operation.Operation == CaseOperation.ReadCase)
            .ExecuteDeleteAsync().ConfigureAwait(false);
    }

    public async Task SetOrganizationStatusAsync(PersistentResourceStatus status)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.Organizations.Where(item => item.OrganizationId == Authority.OrganizationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, status))
            .ConfigureAwait(false);
    }

    public async Task SetWorkspaceStatusAsync(PersistentResourceStatus status)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.Workspaces.Where(item => item.WorkspaceId == Authority.WorkspaceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, status))
            .ConfigureAwait(false);
    }

    public async Task SetMembershipExpiryAsync(DateTimeOffset expiry)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.OrganizationMemberships.Where(item => item.MembershipId == Authority.MembershipId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ExpiresAtUtc, expiry))
            .ConfigureAwait(false);
    }

    public async Task SetMembershipStatusAsync(PersistentMembershipStatus status)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.OrganizationMemberships.Where(item => item.MembershipId == Authority.MembershipId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, status))
            .ConfigureAwait(false);
    }

    public async Task SetMembershipValidFromAsync(DateTimeOffset validFrom)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.OrganizationMemberships.Where(item => item.MembershipId == Authority.MembershipId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ValidFromUtc, validFrom))
            .ConfigureAwait(false);
    }

    public async Task SetGrantSensitivityAsync(string sensitivity)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.AccessGrantGroups.Where(item => item.GrantGroupId == Authority.GrantGroupId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SensitivityCode, sensitivity))
            .ConfigureAwait(false);
    }

    public async Task SetGrantCountryAsync(string countryCode)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.AccessGrantGroups.Where(item => item.GrantGroupId == Authority.GrantGroupId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CountryCode, countryCode))
            .ConfigureAwait(false);
    }

    public async Task SetGrantSectorAsync(Sector sector)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.AccessGrantGroups.Where(item => item.GrantGroupId == Authority.GrantGroupId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Sector, sector))
            .ConfigureAwait(false);
    }

    public async Task SetGrantStatusAsync(PersistentGrantStatus status)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.AccessGrantGroups.Where(item => item.GrantGroupId == Authority.GrantGroupId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, status))
            .ConfigureAwait(false);
    }

    public async Task SetGrantValidFromAsync(DateTimeOffset validFrom)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.AccessGrantGroups.Where(item => item.GrantGroupId == Authority.GrantGroupId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ValidFromUtc, validFrom))
            .ConfigureAwait(false);
    }

    public async Task SetGrantExpiryAsync(DateTimeOffset expiry)
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.AccessGrantGroups.Where(item => item.GrantGroupId == Authority.GrantGroupId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ExpiresAtUtc, expiry))
            .ConfigureAwait(false);
    }

    public async Task RemoveGrantRegionAsync()
    {
        await using var dbContext = Authority.CreateDbContext();
        await dbContext.AccessGrantRegions.Where(item => item.GrantGroupId == Authority.GrantGroupId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
    }

    public async Task RestoreGrantRegionAsync()
    {
        await using var dbContext = Authority.CreateDbContext();
        dbContext.AccessGrantRegions.Add(new AccessGrantRegionEntity
        {
            GrantGroupId = Authority.GrantGroupId,
            RegionId = Authority.GrantRegionId
        });
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_notificationIds.Count > 0 || _eventIds.Count > 0 || _receiptIds.Count > 0 || _payloadIds.Count > 0 || _followUpIds.Count > 0)
        {
            await using var dbContext = Authority.CreateDbContext();
            await using var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
            var reminderNotificationIds = _followUpIds.Count == 0
                ? Array.Empty<Guid>()
                : await dbContext.NotificationItems.AsNoTracking()
                    .Where(item => item.RelatedResourceId != null && _followUpIds.Contains(item.RelatedResourceId.Value))
                    .Select(item => item.NotificationId)
                    .ToArrayAsync().ConfigureAwait(false);
            var notificationIds = _notificationIds.Concat(reminderNotificationIds).Distinct().ToArray();
            await dbContext.NotificationDeliveryOutbox
                .Where(item => notificationIds.Contains(item.NotificationId))
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await dbContext.NotificationItems
                .Where(item => notificationIds.Contains(item.NotificationId))
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await dbContext.ManagedCaseFollowUps.Where(item => _followUpIds.Contains(item.FollowUpId))
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await dbContext.CaseEvents.Where(item => _eventIds.Contains(item.EventId))
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await dbContext.CommandReceipts.Where(item => _receiptIds.Contains(item.ReceiptId))
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await dbContext.ProtectedEventPayloads.Where(item => _payloadIds.Contains(item.PayloadId))
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        await Authority.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class ChangeOwnerAfterNotificationPageRead : DbCommandInterceptor
{
    private readonly string _connectionString;
    private readonly Guid _caseId;
    private readonly string _newOwnerActorId;
    private int _mutationApplied;

    public ChangeOwnerAfterNotificationPageRead(
        string connectionString,
        Guid caseId,
        string newOwnerActorId)
    {
        _connectionString = connectionString;
        _caseId = caseId;
        _newOwnerActorId = newOwnerActorId;
    }

    public bool MutationApplied => Volatile.Read(ref _mutationApplied) == 1;

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("notification_items", StringComparison.Ordinal) &&
            command.CommandText.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            Interlocked.CompareExchange(ref _mutationApplied, 1, 0) == 0)
        {
            await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE managed_cases SET accountable_owner_actor_id = @newOwner WHERE case_id = @caseId";
            var ownerParameter = update.CreateParameter();
            ownerParameter.ParameterName = "newOwner";
            ownerParameter.Value = _newOwnerActorId;
            update.Parameters.Add(ownerParameter);
            var caseParameter = update.CreateParameter();
            caseParameter.ParameterName = "caseId";
            caseParameter.Value = _caseId;
            update.Parameters.Add(caseParameter);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
