using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests;

public sealed class CaseCommandProcessorTests
{
    [Fact]
    public async Task Assignment_acknowledgement_requires_its_own_permission_before_reading_history()
    {
        var command = new AcknowledgeAssignmentCommand(CommandId, 2, 2, SourceOccurredAt, CallerRecordedAt);
        var scenario = CreateScenario(command);
        scenario.AccessPolicy.Decisions.Enqueue(Denied(AccessDecisionCode.GrantMissing));

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Denied, result.Status);
        Assert.Equal([CaseOperation.ReadCase, CaseOperation.AcknowledgeAssignment], Assert.Single(scenario.AccessPolicy.Calls));
        Assert.Equal(["denied-audit"], scenario.Persistence.Calls);
    }

    [Fact]
    public async Task Assignment_acknowledgement_records_database_time_and_replays_the_receipt_without_another_event()
    {
        var command = new AcknowledgeAssignmentCommand(CommandId, 2, 2, SourceOccurredAt, CallerRecordedAt);
        var scenario = CreateScenario(command);
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Accepted, result.Status);
        var append = Assert.Single(scenario.Persistence.Appends);
        var acknowledgement = Assert.IsType<CaseAssignmentAcknowledged>(Assert.Single(append.Events));
        Assert.Equal(DatabaseNow, acknowledgement.RecordedAtUtc);
        Assert.Equal(SourceOccurredAt, acknowledgement.OccurredAtUtc);
        Assert.Equal(2, acknowledgement.AssignmentVersion);
        Assert.All(scenario.AccessPolicy.Calls, operations =>
            Assert.Equal([CaseOperation.ReadCase, CaseOperation.AcknowledgeAssignment], operations));

        var retry = CreateScenario(command, aggregateVersion: 3);
        retry.AccessPolicy.Decisions.Enqueue(Allowed());
        retry.AccessPolicy.Decisions.Enqueue(Allowed());
        retry.Persistence.Receipt = ReceiptFor(retry.Actor, command, [Guid.NewGuid()], 3);
        var replay = await retry.Processor.ProcessAsync(retry.Actor, CaseId, 1, command, CancellationToken.None);
        Assert.Equal(CaseCommandProcessingStatus.Replayed, replay.Status);
        Assert.Empty(retry.Persistence.Appends);
        Assert.DoesNotContain("database-clock", retry.Persistence.Calls);
    }

    private static readonly Guid CaseId = Guid.Parse("2e999189-7530-4859-8da7-15f14309ee31");
    private static readonly Guid CommandId = Guid.Parse("f3f322c5-cafd-47db-9667-3273067544ad");
    private static readonly DateTimeOffset DatabaseNow = new(2026, 10, 27, 12, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CallerRecordedAt = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SourceOccurredAt = new(2026, 10, 26, 18, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task Preliminary_denial_stops_before_header_or_replay_and_commits_only_minimal_audit()
    {
        var scenario = CreateScenario(new CorrectReportCommand(CommandId, 2, "corrected text", "source reason", null, CallerRecordedAt));
        scenario.AccessPolicy.Decisions.Enqueue(Denied(AccessDecisionCode.GrantMissing));

        var result = await scenario.Processor.ProcessAsync(
            scenario.Actor, CaseId, 1, scenario.Command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Denied, result.Status);
        Assert.True(result.DenialAuditDurable);
        Assert.Single(scenario.AccessPolicy.Calls);
        Assert.Equal([CaseOperation.ReadCase, CaseOperation.CorrectReport], scenario.AccessPolicy.Calls[0]);
        Assert.Equal(["denied-audit"], scenario.Persistence.Calls);
        Assert.Single(scenario.Persistence.DeniedAudits);
        Assert.Equal(AccessDecisionCode.GrantMissing, scenario.Persistence.DeniedAudits[0].DecisionCode);
        Assert.True(scenario.UnitOfWork.Leases.Single().Committed);
    }

    [Fact]
    public async Task Full_authorization_denial_stops_before_receipt_lookup()
    {
        var scenario = CreateScenario(new CorrectReportCommand(CommandId, 2, "corrected text", "private reason", null, CallerRecordedAt));
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Denied(AccessDecisionCode.SensitivityOutsideGrant));

        var result = await scenario.Processor.ProcessAsync(
            scenario.Actor, CaseId, 1, scenario.Command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Denied, result.Status);
        Assert.Equal(["header", "replay", "denied-audit"], scenario.Persistence.Calls);
        Assert.Equal([CaseOperation.ReadCase, CaseOperation.CorrectReport], scenario.AccessPolicy.Calls[0]);
        Assert.Equal([CaseOperation.ReadCase, CaseOperation.CorrectReport], scenario.AccessPolicy.Calls[1]);
        Assert.Single(scenario.Persistence.DeniedAudits);
        Assert.DoesNotContain("private reason", scenario.Persistence.DeniedAudits[0].ToString());
    }

    [Fact]
    public async Task Matching_receipt_is_replayed_before_expected_version_and_target_checks()
    {
        var command = new ReopenCaseCommand(CommandId, 2, "reopen", "target-actor", null, CallerRecordedAt);
        var scenario = CreateScenario(command, aggregateVersion: 4);
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.Persistence.Receipt = ReceiptFor(scenario.Actor, command, [Guid.NewGuid()], aggregateVersion: 5);

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Replayed, result.Status);
        Assert.Equal(5, result.AggregateVersion);
        Assert.Empty(scenario.UnitOfWork.BeginHints[0]);
        Assert.DoesNotContain("database-clock", scenario.Persistence.Calls);
        Assert.DoesNotContain("target", scenario.Persistence.Calls);
        Assert.DoesNotContain("append", scenario.Persistence.Calls);
        Assert.False(scenario.UnitOfWork.Leases.Single().Committed);
    }

    [Fact]
    public async Task Receipt_hash_mismatch_conflicts_without_running_the_command()
    {
        var command = new CorrectReportCommand(CommandId, 2, "corrected text", "new reason", null, CallerRecordedAt);
        var scenario = CreateScenario(command);
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.Persistence.Receipt = ReceiptFor(
            scenario.Actor,
            command with { Reason = "different original reason" },
            [Guid.NewGuid()],
            aggregateVersion: 3);

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Conflict, result.Status);
        Assert.DoesNotContain("database-clock", scenario.Persistence.Calls);
        Assert.DoesNotContain("append", scenario.Persistence.Calls);
    }

    [Fact]
    public async Task Unsupported_stored_receipt_schema_is_unavailable_and_not_reinterpreted()
    {
        var command = new CorrectReportCommand(CommandId, 2, "corrected text", "reason", null, CallerRecordedAt);
        var scenario = CreateScenario(command);
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.Persistence.Receipt = ReceiptFor(scenario.Actor, command, [Guid.NewGuid()], aggregateVersion: 3) with
        {
            SchemaVersion = 99
        };

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Unavailable, result.Status);
        Assert.DoesNotContain("database-clock", scenario.Persistence.Calls);
        Assert.DoesNotContain("append", scenario.Persistence.Calls);
    }

    [Fact]
    public async Task Receipt_for_another_case_is_a_generic_conflict()
    {
        var command = new CorrectReportCommand(CommandId, 2, "corrected text", "reason", null, CallerRecordedAt);
        var scenario = CreateScenario(command);
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.Persistence.Receipt = ReceiptFor(scenario.Actor, command, [Guid.NewGuid()], aggregateVersion: 3) with
        {
            CaseId = Guid.NewGuid()
        };

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Conflict, result.Status);
        Assert.DoesNotContain("database-clock", scenario.Persistence.Calls);
        Assert.DoesNotContain("append", scenario.Persistence.Calls);
    }

    [Fact]
    public async Task Stale_expected_version_conflicts_after_authorization_and_receipt_check()
    {
        var command = new CorrectReportCommand(CommandId, 2, "corrected text", "reason", null, CallerRecordedAt);
        var scenario = CreateScenario(command, aggregateVersion: 3);
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Conflict, result.Status);
        Assert.Equal(["header", "replay", "receipt"], scenario.Persistence.Calls);
        Assert.DoesNotContain("database-clock", scenario.Persistence.Calls);
    }

    [Fact]
    public async Task Accepted_events_use_database_recorded_time_and_commit_only_after_staging()
    {
        var command = new CorrectReportCommand(CommandId, 2, "corrected text", "encrypted-only reason", SourceOccurredAt, CallerRecordedAt);
        var scenario = CreateScenario(command);
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Accepted, result.Status);
        Assert.Equal(["header", "replay", "receipt", "database-clock", "append"], scenario.Persistence.Calls);
        var appended = Assert.Single(scenario.Persistence.Appends);
        Assert.Equal(DatabaseNow, appended.Events[0].RecordedAtUtc);
        Assert.Equal(SourceOccurredAt, appended.Events[0].OccurredAtUtc);
        Assert.Equal("encrypted-only reason", ((CaseReportCorrected)appended.Events[0]).Reason);
        Assert.Equal(CallerRecordedAt, appended.CommandSnapshot.Command.RecordedAtUtc);
        Assert.True(scenario.UnitOfWork.Leases.Single().Committed);
    }

    [Fact]
    public async Task Late_discovered_assignment_target_restarts_with_hint_before_target_validation()
    {
        var requestId = Guid.Parse("ef9c21bf-018d-4db3-bbf7-2b757b4b7962");
        var command = new ReviewCaseActionRequestCommand(
            CommandId, 3, requestId, CaseActionReviewOutcome.Approved, null, "approve reassignment", null, CallerRecordedAt);
        var scenario = CreateScenario(
            command,
            aggregateVersion: 3,
            aggregate: CreateAggregateWithPendingReassignment(requestId));
        scenario.UnitOfWork.LockAdditionalHints = true;
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Accepted, result.Status);
        Assert.Equal(2, scenario.UnitOfWork.BeginCount);
        Assert.Contains("future-owner", scenario.UnitOfWork.BeginHints[1]);
        Assert.Equal(
            [CaseOperation.ReadCase, CaseOperation.ReviewProtectedAction, CaseOperation.AssignCase],
            scenario.AccessPolicy.Calls[3]);
        Assert.Equal(1, scenario.Persistence.TargetChecks);
        Assert.Equal("future-owner", Assert.Single(scenario.Persistence.Appends).AssignmentTargetActorId);
        Assert.False(scenario.UnitOfWork.Leases[0].Committed);
        Assert.True(scenario.UnitOfWork.Leases[0].Disposed);
        Assert.True(scenario.UnitOfWork.Leases[1].Committed);
    }

    [Fact]
    public async Task Ineligible_assignment_target_is_audited_without_staging_events()
    {
        var command = new ReopenCaseCommand(CommandId, 3, "reopen", "target-actor", null, CallerRecordedAt);
        var scenario = CreateScenario(command, aggregateVersion: 3, aggregate: CreateResolvedAggregate());
        scenario.UnitOfWork.LockAdditionalHints = true;
        scenario.Persistence.TargetEligible = false;
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Denied, result.Status);
        Assert.True(result.DenialAuditDurable);
        Assert.Equal(AccessDecisionCode.OperationNotGranted, Assert.Single(scenario.Persistence.DeniedAudits).DecisionCode);
        Assert.Empty(scenario.Persistence.Appends);
        Assert.Equal(2, scenario.UnitOfWork.BeginCount);
        Assert.Empty(scenario.UnitOfWork.BeginHints[0]);
        Assert.Collection(
            scenario.UnitOfWork.BeginHints[1],
            hint => Assert.Equal("target-actor", hint));
        Assert.Collection(
            scenario.UnitOfWork.Leases,
            firstLease =>
            {
                Assert.False(firstLease.Committed);
                Assert.True(firstLease.Disposed);
            },
            finalLease =>
            {
                Assert.True(finalLease.Committed);
                Assert.True(finalLease.Disposed);
            });
    }

    [Fact]
    public async Task Non_staged_append_denial_rolls_back_the_lease_without_committing_audit_only()
    {
        var command = new ReopenCaseCommand(CommandId, 3, "reopen", "target-actor", null, CallerRecordedAt);
        var scenario = CreateScenario(command, aggregateVersion: 3, aggregate: CreateResolvedAggregate());
        scenario.UnitOfWork.LockAdditionalHints = true;
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.Persistence.AppendOutcomeOverride = new CaseCommandAppendOutcome(
            CaseCommandAppendStatus.AuthorizationChanged,
            ImmutableArray<Guid>.Empty,
            0,
            0);

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Denied, result.Status);
        Assert.False(result.DenialAuditDurable);
        Assert.Contains("append", scenario.Persistence.Calls);
        Assert.DoesNotContain("denied-audit", scenario.Persistence.Calls);
        Assert.Empty(scenario.Persistence.DeniedAudits);
        Assert.Equal(2, scenario.UnitOfWork.BeginCount);
        Assert.Empty(scenario.UnitOfWork.BeginHints[0]);
        Assert.Collection(
            scenario.UnitOfWork.BeginHints[1],
            hint => Assert.Equal("target-actor", hint));
        Assert.Single(scenario.Persistence.Appends);
        Assert.Collection(
            scenario.UnitOfWork.Leases,
            firstLease =>
            {
                Assert.False(firstLease.Committed);
                Assert.True(firstLease.Disposed);
            },
            finalLease =>
            {
                Assert.False(finalLease.Committed);
                Assert.True(finalLease.Disposed);
            });
    }

    [Fact]
    public async Task Retryable_persistence_race_restarts_the_complete_transaction_at_most_three_times()
    {
        var command = new CorrectReportCommand(CommandId, 2, "corrected text", "reason", null, CallerRecordedAt);
        var scenario = CreateScenario(command);
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.AccessPolicy.Decisions.Enqueue(Allowed());
        scenario.Persistence.HeaderReadsBeforeRace = 3;

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Unavailable, result.Status);
        Assert.Equal(3, scenario.UnitOfWork.BeginCount);
        Assert.All(scenario.UnitOfWork.Leases, lease => Assert.False(lease.Committed));
        Assert.Equal(3, scenario.AccessPolicy.Calls.Count);
    }

    [Fact]
    public async Task Unsupported_command_schema_is_invalid_before_beginning_a_lease()
    {
        var scenario = CreateScenario(new CorrectReportCommand(CommandId, 2, "corrected text", "reason", null, CallerRecordedAt));

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 2, scenario.Command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Invalid, result.Status);
        Assert.Equal(0, scenario.UnitOfWork.BeginCount);
    }

    [Fact]
    public async Task Failed_denial_audit_is_unavailable_and_never_reported_durable()
    {
        var scenario = CreateScenario(new CorrectReportCommand(CommandId, 2, "corrected text", "reason", null, CallerRecordedAt));
        scenario.AccessPolicy.Decisions.Enqueue(Denied(AccessDecisionCode.GrantMissing));
        scenario.Persistence.ThrowOnDeniedAudit = true;

        var result = await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, scenario.Command, CancellationToken.None);

        Assert.Equal(CaseCommandProcessingStatus.Unavailable, result.Status);
        Assert.False(result.DenialAuditDurable);
        Assert.False(scenario.UnitOfWork.Leases.Single().Committed);
        Assert.True(scenario.UnitOfWork.Leases.Single().Disposed);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_retrying_or_committing()
    {
        var scenario = CreateScenario(new CorrectReportCommand(CommandId, 2, "corrected text", "reason", null, CallerRecordedAt));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await scenario.Processor.ProcessAsync(scenario.Actor, CaseId, 1, scenario.Command, cancellationSource.Token));

        Assert.Equal(0, scenario.UnitOfWork.BeginCount);
    }

    private static Scenario CreateScenario(
        CaseCommand command,
        long aggregateVersion = 2,
        CaseAggregate? aggregate = null)
    {
        var actor = CreateActor("staff-actor");
        var unitOfWork = new FakeUnitOfWork(actor.ActorId);
        var accessPolicy = new FakeAccessPolicy();
        var persistence = new FakePersistence
        {
            Header = new CaseCommandCaseHeader(CaseId, aggregateVersion, 1, 1, "staff-actor"),
            Aggregate = aggregate ?? CreateAggregate(aggregateVersion)
        };
        var processor = new CaseCommandProcessor(unitOfWork, accessPolicy, persistence);
        return new Scenario(actor, command, unitOfWork, accessPolicy, persistence, processor);
    }

    private static CaseAggregate CreateAggregate(long version = 2)
    {
        var acceptanceCommandId = Guid.NewGuid();
        var events = new List<CaseEvent>
        {
            new CaseReported(CaseId, 1, 1, acceptanceCommandId, "staff-actor", null, DatabaseNow,
                new ReportFacts(CaseKind.Human, Sector.HumanHealth, "synthetic source", [new ReportedObservation("synthetic observation")]),
                "workspace-a"),
            new CaseAssigned(CaseId, 2, 1, acceptanceCommandId, "staff-actor", null, DatabaseNow, "staff-actor")
        };
        for (var nextVersion = 3; nextVersion <= version; nextVersion++)
        {
            events.Add(new CaseActionRequested(CaseId, nextVersion, 1, Guid.NewGuid(), "staff-actor", null, DatabaseNow,
                Guid.NewGuid(), CaseAction.Resolve, "synthetic request", [], null));
        }

        return CaseAggregate.Replay(events);
    }

    private static CaseAggregate CreateResolvedAggregate()
    {
        var acceptanceCommandId = Guid.NewGuid();
        return CaseAggregate.Replay(
        [
            new CaseReported(CaseId, 1, 1, acceptanceCommandId, "staff-actor", null, DatabaseNow,
                new ReportFacts(CaseKind.Human, Sector.HumanHealth, "synthetic source", [new ReportedObservation("synthetic observation")]),
                "workspace-a"),
            new CaseAssigned(CaseId, 2, 1, acceptanceCommandId, "staff-actor", null, DatabaseNow, "staff-actor"),
            new CaseResolved(CaseId, 3, 1, Guid.NewGuid(), "staff-actor", null, DatabaseNow, "synthetic resolution")
        ]);
    }

    private static CaseAggregate CreateAggregateWithPendingReassignment(Guid requestId)
    {
        var acceptanceCommandId = Guid.NewGuid();
        return CaseAggregate.Replay(
        [
        new CaseReported(CaseId, 1, 1, acceptanceCommandId, "staff-actor", null, DatabaseNow,
            new ReportFacts(CaseKind.Human, Sector.HumanHealth, "synthetic source", [new ReportedObservation("synthetic observation")]),
            "workspace-a"),
        new CaseAssigned(CaseId, 2, 1, acceptanceCommandId, "staff-actor", null, DatabaseNow, "staff-actor"),
        new CaseActionRequested(CaseId, 3, 1, Guid.NewGuid(), "staff-actor", null, DatabaseNow,
            requestId, CaseAction.Reassign, "synthetic request", [], "future-owner")
        ]);
    }

    private static ActorContext CreateActor(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private static AccessDecision Allowed() => new(true, AccessDecisionCode.Allowed, null)
    {
        MatchedGrantGroupId = "grant-group-a"
    };

    private static AccessDecision Denied(AccessDecisionCode code) => new(false, code, null);

    private static CaseCommandReceipt ReceiptFor(
        ActorContext actor,
        CaseCommand command,
        IEnumerable<Guid> eventIds,
        long aggregateVersion)
    {
        var snapshot = CanonicalCaseCommandHasher.CreateSnapshot(
            actor,
            CaseId,
            CanonicalCaseCommandHasher.CurrentReceiptProtocolVersion,
            CanonicalCaseCommandHasher.CurrentWireCommandSchemaVersion,
            command);
        return new CaseCommandReceipt(
            actor.ActorId,
            command.CommandId,
            CaseId,
            snapshot.CommandType,
            CanonicalCaseCommandHasher.CurrentReceiptProtocolVersion,
            snapshot.CanonicalPayloadHash.ToImmutableArray(),
            eventIds.ToImmutableArray(),
            aggregateVersion,
            1);
    }

    private sealed record Scenario(
        ActorContext Actor,
        CaseCommand Command,
        FakeUnitOfWork UnitOfWork,
        FakeAccessPolicy AccessPolicy,
        FakePersistence Persistence,
        CaseCommandProcessor Processor);

    private sealed class FakeUnitOfWork(string actorId) : ICaseCommandUnitOfWork
    {
        public List<FakeLease> Leases { get; } = [];
        public List<ImmutableArray<string>> BeginHints { get; } = [];
        public int BeginCount { get; private set; }
        public bool LockAdditionalHints { get; set; }

        public ValueTask<ICaseCommandLease> BeginCaseAsync(
            ActorContext actor,
            Guid caseId,
            ImmutableArray<string> additionalActorHints,
            CaseLeaseMode mode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCount++;
            BeginHints.Add(additionalActorHints);
            var lockedActors = LockAdditionalHints
                ? additionalActorHints.Add(actorId).Distinct(StringComparer.Ordinal).ToImmutableArray()
                : [actorId];
            var lease = new FakeLease(caseId, mode, lockedActors);
            Leases.Add(lease);
            return ValueTask.FromResult<ICaseCommandLease>(lease);
        }
    }

    private sealed class FakeLease(Guid caseId, CaseLeaseMode mode, ImmutableArray<string> lockedActorIds) : ICaseCommandLease
    {
        public Guid CaseId { get; } = caseId;
        public CaseLeaseMode Mode { get; } = mode;
        public ImmutableArray<string> LockedActorIds { get; } = lockedActorIds;
        public bool IsActive { get; private set; } = true;
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Committed = true;
            IsActive = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsActive = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAccessPolicy : ICaseAccessPolicy
    {
        public Queue<AccessDecision> Decisions { get; } = new();
        public List<IReadOnlyCollection<CaseOperation>> Calls { get; } = [];

        public ValueTask<AccessDecision> AuthorizeAsync(
            ActorContext actor,
            Guid caseId,
            CaseOperation operation,
            CancellationToken cancellationToken) => AuthorizeAllAsync(actor, caseId, [operation], cancellationToken);

        public ValueTask<AccessDecision> AuthorizeAllAsync(
            ActorContext actor,
            Guid caseId,
            IReadOnlyCollection<CaseOperation> requiredOperations,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(requiredOperations.ToArray());
            return ValueTask.FromResult(Decisions.Count > 0 ? Decisions.Dequeue() : Allowed());
        }
    }

    private sealed class FakePersistence : ICaseCommandPersistence
    {
        public List<string> Calls { get; } = [];
        public List<CaseCommandAppendRequest> Appends { get; } = [];
        public List<CaseCommandDenialAudit> DeniedAudits { get; } = [];
        public CaseCommandCaseHeader? Header { get; set; }
        public CaseAggregate Aggregate { get; set; } = CreateAggregate();
        public CaseCommandReceipt? Receipt { get; set; }
        public DateTimeOffset DatabaseNow { get; set; } = CaseCommandProcessorTests.DatabaseNow;
        public bool TargetEligible { get; set; } = true;
        public CaseCommandAppendOutcome? AppendOutcomeOverride { get; set; }
        public int TargetChecks { get; private set; }
        public int HeaderReadsBeforeRace { get; set; }
        public bool ThrowOnDeniedAudit { get; set; }

        public ValueTask<CaseCommandCaseHeader?> ReadCaseHeaderAsync(
            ICaseCommandLease lease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("header");
            if (HeaderReadsBeforeRace > 0)
            {
                HeaderReadsBeforeRace--;
                throw new CasePersistenceRaceException(CasePersistenceRaceKind.Deadlock);
            }

            return ValueTask.FromResult(Header);
        }

        public ValueTask<CaseAggregate> ReplayCaseAsync(ICaseCommandLease lease, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("replay");
            return ValueTask.FromResult(Aggregate);
        }

        public ValueTask<CaseCommandReceipt?> FindReceiptAsync(
            ICaseCommandLease lease,
            string actorId,
            Guid commandId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("receipt");
            return ValueTask.FromResult(Receipt);
        }

        public ValueTask<DateTimeOffset> GetDatabaseUtcNowAsync(ICaseCommandLease lease, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("database-clock");
            return ValueTask.FromResult(DatabaseNow);
        }

        public ValueTask<bool> IsEligibleAssignmentTargetAsync(
            ICaseCommandLease lease,
            string targetActorId,
            CaseCommandCaseHeader caseHeader,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("target");
            TargetChecks++;
            return ValueTask.FromResult(TargetEligible);
        }

        public ValueTask<CaseCommandAppendOutcome> StageAcceptedCommandAsync(
            ICaseCommandLease lease,
            CaseCommandAppendRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("append");
            Appends.Add(request);
            if (AppendOutcomeOverride is not null)
            {
                return ValueTask.FromResult(AppendOutcomeOverride);
            }

            return ValueTask.FromResult(CaseCommandAppendOutcome.Staged(
                request.Events.Select(@event => @event.EventId),
                request.Events[^1].AggregateVersion,
                request.Events[^1].ReportRevision));
        }

        public ValueTask StageDeniedAuditAsync(
            ICaseCommandLease lease,
            CaseCommandDenialAudit audit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnDeniedAudit)
            {
                throw new InvalidOperationException("Synthetic audit store failure.");
            }

            Calls.Add("denied-audit");
            DeniedAudits.Add(audit);
            return ValueTask.CompletedTask;
        }
    }
}
