using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Events;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests for the P5.2 case-monitoring persistence slice: observation,
/// follow-up, and escalation commands stage journal rows and maintain the monitoring
/// read models inside the same lease transaction, and the monitoring summary read
/// re-verifies <c>ReadCase</c>. Uses the same disposable loopback database gate as
/// <see cref="IntakePostgresFactAttribute"/>.
/// </summary>
[Trait("Category", "IntakePostgres")]
[Collection(IntakePostgresCollection.Name)]
public sealed class CaseMonitoringPostgresTests
{
    private static readonly DateTimeOffset FirstDueAtUtc = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondDueAtUtc = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    [IntakePostgresFact]
    public async Task AssignedWorkerAcknowledgement_PersistsAndRemainsDueInAuthorizedQueue()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");
        var followUpId = Guid.NewGuid();

        var scheduled = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, followUpId, "record next observation", "officer-1",
                FirstDueAtUtc, "staff scheduled", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);
        var acknowledged = await ProcessCommandAsync(db, actor, caseId,
            new AcknowledgeFollowUpCommand(Guid.NewGuid(), 3, followUpId, null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, acknowledged.Status);
        Assert.Equal(4, acknowledged.AggregateVersion);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var projection = await verify.ManagedCaseFollowUps.AsNoTracking().SingleAsync(item => item.FollowUpId == followUpId);
        Assert.Equal(FollowUpStatus.Acknowledged, projection.Status);
        var eventRow = await verify.CaseEvents.AsNoTracking().SingleAsync(item => item.AggregateVersion == 4 && item.CaseId == caseId);
        Assert.Equal(nameof(CaseEventType.CaseFollowUpAcknowledged), eventRow.EventType);
        Assert.Equal("officer-1", eventRow.ActorId);

        var queue = await QueryQueueAsync(actor, FirstDueAtUtc.AddDays(2));
        var item = Assert.Single(queue.Items);
        Assert.Equal("Acknowledged", item.Status);
        Assert.True(item.IsOverdue);
    }

    [IntakePostgresFact]
    public async Task ScheduleFollowUp_then_CompletedOutcome_closes_follow_up()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var followUpId = Guid.NewGuid();
        var schedule = new ScheduleFollowUpCommand(
            Guid.NewGuid(), 2, followUpId, "revisit holding", "worker-1",
            FirstDueAtUtc, "routine check", null, DateTimeOffset.UtcNow);
        var scheduled = await ProcessCommandAsync(db, actor, caseId, schedule, keyRing.CreateOptions(), provider);

        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);
        Assert.Equal(3, scheduled.AggregateVersion);

        var completed = new RecordFollowUpOutcomeCommand(
            Guid.NewGuid(), 3, followUpId, FollowUpOutcome.Completed,
            "holding calm, no concerns", null, "visit complete", null, DateTimeOffset.UtcNow);
        var outcome = await ProcessCommandAsync(db, actor, caseId, completed, keyRing.CreateOptions(), provider);

        Assert.Equal(CaseCommandProcessingStatus.Accepted, outcome.Status);
        Assert.Equal(4, outcome.AggregateVersion);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var row = await verify.ManagedCaseFollowUps.AsNoTracking()
            .SingleAsync(item => item.FollowUpId == followUpId);
        Assert.Equal(caseId, row.CaseId);
        Assert.Equal("revisit holding", row.Purpose);
        Assert.Equal("worker-1", row.AssigneeActorId);
        Assert.Equal(FirstDueAtUtc, row.DueAtUtc);
        Assert.Equal(FollowUpStatus.Completed, row.Status);
        Assert.Equal(3, row.ScheduledVersion);
        Assert.Equal(4, row.OutcomeVersion);

        var summary = await QuerySummaryAsync(db, actor, caseId);
        Assert.Equal(CaseMonitoringSummaryAccess.Found, summary.Access);
        Assert.NotNull(summary.Summary);
        Assert.Empty(summary.Summary!.OpenFollowUps);
        Assert.Empty(summary.Summary.Escalations);
        Assert.Null(summary.Summary.LatestObservation);
    }

    [IntakePostgresFact]
    public async Task MissedOutcome_then_Rescheduled_preserves_prior_due()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var followUpId = Guid.NewGuid();
        var scheduled = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, followUpId, "revisit holding", "worker-1",
                FirstDueAtUtc, "routine check", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);
        var scheduleEventId = Assert.Single(scheduled.EventIds);

        var missed = await ProcessCommandAsync(db, actor, caseId,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 3, followUpId, FollowUpOutcome.Missed,
                "nobody present at holding", null, "attempt logged", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, missed.Status);
        Assert.Equal(4, missed.AggregateVersion);

        await using (var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync())
        {
            var row = await verify.ManagedCaseFollowUps.AsNoTracking()
                .SingleAsync(item => item.FollowUpId == followUpId);
            Assert.Equal(FollowUpStatus.Missed, row.Status);
            Assert.Equal(FirstDueAtUtc, row.DueAtUtc);
            Assert.Equal(4, row.OutcomeVersion);
            Assert.Equal(3, row.CurrentScheduleVersion);
            Assert.Equal(scheduleEventId, row.CurrentScheduleEventId);
        }

        var openAfterMiss = await QuerySummaryAsync(db, actor, caseId);
        var missedItem = Assert.Single(openAfterMiss.Summary!.OpenFollowUps);
        Assert.Equal(followUpId, missedItem.FollowUpId);
        Assert.Equal("Missed", missedItem.Status);
        Assert.Equal(FirstDueAtUtc, missedItem.DueAtUtc);

        var rescheduled = await ProcessCommandAsync(db, actor, caseId,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 4, followUpId, FollowUpOutcome.Rescheduled,
                "retry after market day", SecondDueAtUtc, "new attempt", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, rescheduled.Status);
        Assert.Equal(5, rescheduled.AggregateVersion);
        var rescheduledEventId = Assert.Single(rescheduled.EventIds);

        await using (var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync())
        {
            var row = await verify.ManagedCaseFollowUps.AsNoTracking()
                .SingleAsync(item => item.FollowUpId == followUpId);
            Assert.Equal(FollowUpStatus.Scheduled, row.Status);
            Assert.Equal(SecondDueAtUtc, row.DueAtUtc);
            Assert.Equal(3, row.ScheduledVersion);
            Assert.Equal(5, row.OutcomeVersion);
            Assert.Equal(5, row.CurrentScheduleVersion);
            Assert.Equal(rescheduledEventId, row.CurrentScheduleEventId);

            var outcomeEvents = await verify.CaseEvents.AsNoTracking()
                .Where(item => item.CaseId == caseId && item.EventType == "CaseFollowUpOutcomeRecorded")
                .OrderBy(item => item.AggregateVersion)
                .Select(item => item.AggregateVersion)
                .ToArrayAsync();
            Assert.Equal([4L, 5L], outcomeEvents);
        }

        var openAfterReschedule = await QuerySummaryAsync(db, actor, caseId);
        var rescheduledItem = Assert.Single(openAfterReschedule.Summary!.OpenFollowUps);
        Assert.Equal(followUpId, rescheduledItem.FollowUpId);
        Assert.Equal("Scheduled", rescheduledItem.Status);
        Assert.Equal(SecondDueAtUtc, rescheduledItem.DueAtUtc);
    }

    [IntakePostgresFact]
    public async Task UnreachableOutcome_does_not_complete_follow_up()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var followUpId = Guid.NewGuid();
        var scheduled = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, followUpId, "contact reporter", "worker-1",
                FirstDueAtUtc, "callback", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);

        var unreachable = await ProcessCommandAsync(db, actor, caseId,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 3, followUpId, FollowUpOutcome.Unreachable,
                "phone off, holding gate locked", null, "attempt logged", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, unreachable.Status);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var row = await verify.ManagedCaseFollowUps.AsNoTracking()
            .SingleAsync(item => item.FollowUpId == followUpId);
        Assert.Equal(FollowUpStatus.Unreachable, row.Status);
        Assert.Equal(FirstDueAtUtc, row.DueAtUtc);

        var summary = await QuerySummaryAsync(db, actor, caseId);
        var item = Assert.Single(summary.Summary!.OpenFollowUps);
        Assert.Equal("Unreachable", item.Status);
    }

    [IntakePostgresFact]
    public async Task Escalation_raise_acknowledge_close_keeps_owner()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.RecordObservation, CaseOperation.ManageEscalation]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var observationId = Guid.NewGuid();
        var observed = await ProcessCommandAsync(db, actor, caseId,
            new RecordObservationCommand(Guid.NewGuid(), 2, observationId,
                "holding gate locked two visits running", "home-visit", "visit note", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, observed.Status);
        var evidenceId = Assert.Single(observed.EventIds).ToString("D");

        var escalationId = Guid.NewGuid();
        var raised = await ProcessCommandAsync(db, actor, caseId,
            new RaiseEscalationCommand(Guid.NewGuid(), 3, escalationId,
                "holding unreachable twice", [evidenceId], "duty-officer", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, raised.Status);
        Assert.Equal(4, raised.AggregateVersion);

        var acknowledged = await ProcessCommandAsync(db, actor, caseId,
            new AcknowledgeEscalationCommand(Guid.NewGuid(), 4, escalationId,
                "supervisor paged", "acknowledged", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, acknowledged.Status);

        var closed = await ProcessCommandAsync(db, actor, caseId,
            new CloseEscalationCommand(Guid.NewGuid(), 5, escalationId,
                "supervisor visited holding", "resolved", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, closed.Status);
        Assert.Equal(6, closed.AggregateVersion);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var row = await verify.ManagedCaseEscalations.AsNoTracking()
            .SingleAsync(item => item.EscalationId == escalationId);
        Assert.Equal(caseId, row.CaseId);
        Assert.Equal(EscalationStatus.Closed, row.Status);
        Assert.Equal(4, row.RaisedVersion);
        Assert.Equal(5, row.AcknowledgedVersion);
        Assert.Equal(6, row.ClosedVersion);

        var projection = await verify.ManagedCases.AsNoTracking()
            .SingleAsync(item => item.CaseId == caseId);
        Assert.Equal("officer-1", projection.AccountableOwnerActorId);
        var epochs = await verify.ActorAccessStates.AsNoTracking()
            .Where(item => item.ActorId == "officer-1")
            .Select(item => item.AccessEpoch)
            .ToArrayAsync();
        Assert.Equal([7L], epochs);

        var summary = await QuerySummaryAsync(db, actor, caseId);
        var item = Assert.Single(summary.Summary!.Escalations);
        Assert.Equal(escalationId, item.EscalationId);
        Assert.Equal("Closed", item.Status);
    }

    [IntakePostgresFact]
    public async Task RecordObservation_stored_and_summarized_as_latest()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.RecordObservation]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var firstId = Guid.NewGuid();
        var first = await ProcessCommandAsync(db, actor, caseId,
            new RecordObservationCommand(Guid.NewGuid(), 2, firstId,
                "subject resting, breathing normal", "home-visit", "visit note", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, first.Status);

        var secondId = Guid.NewGuid();
        var second = await ProcessCommandAsync(db, actor, caseId,
            new RecordObservationCommand(Guid.NewGuid(), 3, secondId,
                "water point functional", "phone-check", "callback note", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, second.Status);

        await using (var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync())
        {
            Assert.Equal(2, await verify.ManagedCaseObservations.AsNoTracking()
                .CountAsync(item => item.CaseId == caseId));
            var firstRow = await verify.ManagedCaseObservations.AsNoTracking()
                .SingleAsync(item => item.ObservationId == firstId);
            Assert.Equal("subject resting, breathing normal", firstRow.Narrative);
            Assert.Equal("home-visit", firstRow.Source);
            Assert.Equal(3, firstRow.AggregateVersion);
        }

        var summary = await QuerySummaryAsync(db, actor, caseId);
        Assert.NotNull(summary.Summary!.LatestObservation);
        Assert.Equal(secondId, summary.Summary.LatestObservation!.ObservationId);
        Assert.Equal("water point functional", summary.Summary.LatestObservation.Narrative);
        Assert.Equal("phone-check", summary.Summary.LatestObservation.Source);
        Assert.Equal(4, summary.Summary.LatestObservation.AggregateVersion);
    }

    [IntakePostgresFact]
    public async Task MonitoringCommand_replay_is_idempotent()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.RecordObservation]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var command = new RecordObservationCommand(Guid.NewGuid(), 2, Guid.NewGuid(),
            "subject resting, breathing normal", "home-visit", "visit note", null, DateTimeOffset.UtcNow);
        var first = await ProcessCommandAsync(db, actor, caseId, command, keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, first.Status);

        var replay = await ProcessCommandAsync(db, actor, caseId, command, keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Replayed, replay.Status);
        Assert.True(first.EventIds.SequenceEqual(replay.EventIds));
        Assert.Equal(first.AggregateVersion, replay.AggregateVersion);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        Assert.Equal(1, await verify.ManagedCaseObservations.AsNoTracking()
            .CountAsync(item => item.CaseId == caseId));
        Assert.Equal(1, await verify.CaseEvents.AsNoTracking()
            .CountAsync(item => item.CaseId == caseId && item.CommandId == command.CommandId));
    }

    [IntakePostgresFact]
    public async Task StaleExpectedVersion_conflicts()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.RecordObservation, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var observed = await ProcessCommandAsync(db, actor, caseId,
            new RecordObservationCommand(Guid.NewGuid(), 2, Guid.NewGuid(),
                "subject resting, breathing normal", "home-visit", "visit note", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, observed.Status);
        Assert.Equal(3, observed.AggregateVersion);

        var stale = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, Guid.NewGuid(), "revisit holding", "worker-1",
                FirstDueAtUtc, "routine check", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Conflict, stale.Status);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        Assert.Equal(0, await verify.ManagedCaseFollowUps.AsNoTracking()
            .CountAsync(item => item.CaseId == caseId));
    }

    [IntakePostgresFact]
    public async Task MissingMonitoringOperation_denies_command()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db, [CaseOperation.ReadCase]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var denied = await ProcessCommandAsync(db, actor, caseId,
            new RecordObservationCommand(Guid.NewGuid(), 2, Guid.NewGuid(),
                "subject resting, breathing normal", "home-visit", "visit note", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Denied, denied.Status);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        Assert.Equal(0, await verify.ManagedCaseObservations.AsNoTracking()
            .CountAsync(item => item.CaseId == caseId));
        Assert.Equal(0, await verify.CaseEvents.AsNoTracking()
            .CountAsync(item => item.CaseId == caseId && item.AggregateVersion > 2));
    }

    [IntakePostgresFact]
    public async Task Summary_without_read_is_not_found_or_not_authorized()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db, [CaseOperation.RecordObservation]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var denied = await QuerySummaryAsync(db, actor, caseId);
        Assert.Equal(CaseMonitoringSummaryAccess.NotFoundOrNotAuthorized, denied.Access);
        Assert.Null(denied.Summary);

        var unknown = await QuerySummaryAsync(db, actor, Guid.NewGuid());
        Assert.Equal(CaseMonitoringSummaryAccess.NotFoundOrNotAuthorized, unknown.Access);
        Assert.Null(unknown.Summary);
    }

    private static ActorContext CreateOfficer(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private const string MonitoringRegionId = "region-zw-harare";

    private static async Task<string> SeedMonitoringScopeAsync(
        SwiftAidDbContext db, IReadOnlyList<CaseOperation> operations)
    {
        await CaseRegistrationBackendTests.SeedRegistrationIdentitiesAsync(db);
        var membershipId = await IntakeDecisionStoreTests.ResetOfficerScopeAsync(db, "officer-1", 7);

        var grantGroupId = $"grant-group-monitoring-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        db.AccessGrantGroups.Add(new AccessGrantGroupEntity
        {
            GrantGroupId = grantGroupId,
            MembershipId = membershipId,
            ActorId = "officer-1",
            OrganizationId = "org-zw-1",
            WorkspaceId = "workspace-1",
            CountryCode = "ZW",
            Sector = Sector.HumanHealth,
            SensitivityCode = "standard",
            CanAccessJurisdictionalCases = false,
            Status = PersistentGrantStatus.Active,
            ValidFromUtc = now.AddHours(-1),
            ExpiresAtUtc = now.AddHours(1),
            GrantedByActorId = "officer-1",
            Version = 1
        });
        foreach (var operation in operations.Distinct())
        {
            db.AccessGrantOperations.Add(new AccessGrantOperationEntity
            {
                GrantGroupId = grantGroupId,
                Operation = operation
            });
        }

        // The shared case resource policy requires every case region to be covered by
        // the grant regions, including for the accountable owner, so the grant carries
        // the same region seeded onto the monitored case below.
        db.AccessGrantRegions.Add(new AccessGrantRegionEntity
        {
            GrantGroupId = grantGroupId,
            RegionId = MonitoringRegionId
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return grantGroupId;
    }

    private static async Task<Guid> SeedMonitoredCaseAsync(
        SwiftAidDbContext db,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider provider)
    {
        var caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        await db.ManagedCases
            .Where(item => item.CaseId == caseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.WorkflowVersion, 1));
        db.ManagedCaseRegions.Add(new ManagedCaseRegionEntity
        {
            CaseId = caseId,
            RegionId = MonitoringRegionId
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await SeedInitialJournalAsync(db, caseId, options, provider);
        return caseId;
    }

    private static async Task SeedInitialJournalAsync(
        SwiftAidDbContext db,
        Guid caseId,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider provider)
    {
        var actor = CreateOfficer("officer-1");
        var unitOfWork = new PostgresCaseCommandUnitOfWork(db);
        var store = new PostgresProtectedCaseEventPayloadStore(db, unitOfWork, options, provider);
        var serializer = new CaseEventSerializer();
        await using var lease = await unitOfWork.BeginCaseAsync(
            actor, caseId, [], CaseLeaseMode.Write, CancellationToken.None);
        var commandId = Guid.NewGuid();
        var recordedAt = DateTimeOffset.UtcNow;
        var report = new ReportFacts(
            CaseKind.Human,
            Sector.HumanHealth,
            "synthetic protected report text",
            Array.AsReadOnly<ReportedObservation>([new ReportedObservation("synthetic observation")]));
        var initialEvents = CaseAggregate.Create(
            new AcceptCaseCommand(
                commandId, caseId, report, "officer-1", "workspace-1", null, recordedAt),
            new ActorCapabilities("officer-1", CaseCapability.AcceptReport | CaseCapability.AssignCase));

        db.CommandReceipts.Add(new CommandReceiptEntity
        {
            ReceiptId = Guid.NewGuid(),
            ActorId = "officer-1",
            CommandId = commandId,
            CaseId = caseId,
            CommandType = "AcceptCase",
            SchemaVersion = 1,
            CanonicalPayloadHash = SHA256.HashData([]),
            FirstAggregateVersion = 1,
            LastAggregateVersion = 2,
            ReportRevision = 1,
            AcceptedAtUtc = recordedAt
        });

        foreach (var @event in initialEvents)
        {
            var eventType = @event.GetType().Name;
            var serialized = serializer.SerializeToUtf8Bytes(@event);
            try
            {
                var payloadId = Guid.NewGuid();
                var reference = await store.StageProtectedAsync(
                    caseId, payloadId, eventType, 1, serialized, CancellationToken.None);
                db.CaseEvents.Add(new CaseEventEntity
                {
                    EventId = @event.EventId,
                    CaseId = @event.CaseId,
                    AggregateVersion = @event.AggregateVersion,
                    ReportRevision = @event.ReportRevision,
                    CommandId = @event.CommandId,
                    ActorId = @event.ActorId,
                    EventType = eventType,
                    SchemaVersion = 1,
                    OccurredAtUtc = @event.OccurredAtUtc,
                    RecordedAtUtc = @event.RecordedAtUtc,
                    PayloadId = reference.PayloadId
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serialized);
            }
        }

        await lease.CommitAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    private static async Task<CaseCommandProcessingResult> ProcessCommandAsync(
        SwiftAidDbContext db,
        ActorContext actor,
        Guid caseId,
        CaseCommand command,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider provider)
    {
        await using var context = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(context);
        var payloadStore = new PostgresProtectedCaseEventPayloadStore(context, unitOfWork, options, provider);
        var persistence = new PostgresCaseCommandPersistence(
            context, unitOfWork, payloadStore, new CaseEventSerializer());
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(context, unitOfWork),
            new PostgresCaseAccessDescriptorStore(context, unitOfWork));
        var processor = new CaseCommandProcessor(unitOfWork, policy, persistence);
        return await processor.ProcessAsync(actor, caseId, 1, command, CancellationToken.None);
    }

    private static async Task<CaseMonitoringSummaryResult> QuerySummaryAsync(
        SwiftAidDbContext db, ActorContext actor, Guid caseId)
    {
        await using var context = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(context);
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(context, unitOfWork),
            new PostgresCaseAccessDescriptorStore(context, unitOfWork));
        var query = new CaseMonitoringSummaryQuery(
            new PostgresCaseMonitoringSummaryStore(context), policy, unitOfWork);
        return await query.GetAsync(actor, caseId, CancellationToken.None);
    }

    private static async Task<MonitoringQueueResult> QueryQueueAsync(ActorContext actor, DateTimeOffset utcNow)
    {
        await using var context = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(context);
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(context, unitOfWork),
            new PostgresCaseAccessDescriptorStore(context, unitOfWork));
        var query = new CaseMonitoringQueueQuery(new PostgresCaseMonitoringQueueStore(context), policy, unitOfWork);
        return await query.GetAssignedAsync(actor, null, utcNow, CancellationToken.None);
    }

    private sealed class CaseMonitoringCleanup(Guid caseId, string grantGroupId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var context = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
            var payloadIds = await context.CaseEvents.AsNoTracking()
                .Where(item => item.CaseId == caseId)
                .Select(item => item.PayloadId)
                .ToArrayAsync();
            var eventIds = await context.CaseEvents.AsNoTracking()
                .Where(item => item.CaseId == caseId)
                .Select(item => item.EventId)
                .ToArrayAsync();
            await context.DomainOutbox.Where(item => eventIds.Contains(item.EventId))
                .ExecuteDeleteAsync();
            await context.ManagedCaseObservations.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.ManagedCaseFollowUps.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.ManagedCaseEscalations.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.CaseEvents.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.CommandReceipts.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.ProtectedEventPayloads.Where(item => payloadIds.Contains(item.PayloadId))
                .ExecuteDeleteAsync();
            await context.SecurityAuditEntries.Where(item => item.ResourceId == caseId)
                .ExecuteDeleteAsync();
            await context.NotificationItems.Where(item => item.ResourceId == caseId)
                .ExecuteDeleteAsync();
            await context.ManagedCaseRegions.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.ManagedCases.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.AccessGrantOperations.Where(item => item.GrantGroupId == grantGroupId)
                .ExecuteDeleteAsync();
            await context.AccessGrantRegions.Where(item => item.GrantGroupId == grantGroupId)
                .ExecuteDeleteAsync();
            await context.AccessGrantGroups.Where(item => item.GrantGroupId == grantGroupId)
                .ExecuteDeleteAsync();
        }
    }

    private sealed class SyntheticMonitoringKeyRing : IDisposable
    {
        private readonly string _rootPath;
        private readonly string _certificatePath;
        private readonly string _certificatePassword;

        private SyntheticMonitoringKeyRing(string rootPath, string keysPath, string certificatePath, string certificatePassword)
        {
            _rootPath = rootPath;
            KeysPath = keysPath;
            _certificatePath = certificatePath;
            _certificatePassword = certificatePassword;
        }

        public string KeysPath { get; }

        public static SyntheticMonitoringKeyRing Create()
        {
            var root = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-monitoring");
            var keys = PrivateKeyRingTestDirectory.CreateKeyDirectory(root);
            var certificatePath = Path.Combine(root, "protection.pfx");
            try
            {
                var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=SwiftAid synthetic monitoring", rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
                var bytes = certificate.Export(X509ContentType.Pfx, password);
                try
                {
                    File.WriteAllBytes(certificatePath, bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }

                return new SyntheticMonitoringKeyRing(root, keys, certificatePath, password);
            }
            catch
            {
                PrivateKeyRingTestDirectory.DeleteRoot(root);
                throw;
            }
        }

        public ProtectedPayloadOptions CreateOptions() => new()
        {
            Enabled = true,
            ApplicationName = "SwiftAid.SyntheticMonitoringTests",
            KeysPath = KeysPath,
            CertificatePath = _certificatePath,
            CertificatePassword = _certificatePassword,
            MaximumPayloadBytes = 1024 * 1024
        };

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SwiftAid.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
