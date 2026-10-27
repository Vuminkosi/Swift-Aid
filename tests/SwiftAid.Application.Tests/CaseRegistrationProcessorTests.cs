using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Contracts.Geography;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests;

public sealed class CaseRegistrationProcessorTests
{
    private static readonly Guid CommandId = Guid.Parse("c77abff4-06eb-4a79-80b5-86d2f4b3f32c");
    private static readonly Guid ExistingCaseId = Guid.Parse("7b3d8111-bbb0-417c-93a4-1cdb8ff0d8cc");
    private static readonly DateTimeOffset DatabaseNow = new(2026, 10, 27, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task Authorized_registration_creates_case_with_trusted_actor_as_initial_owner()
    {
        var scenario = new Scenario();
        scenario.Authorization.NewDecisions.Enqueue(Allowed());
        scenario.Authorization.NewDecisions.Enqueue(Allowed());
        scenario.Persistence.CandidateCaseIds.Enqueue(null);
        scenario.Persistence.AppendStatus = CaseRegistrationAppendStatus.Staged;

        var result = await scenario.Processor.RegisterAsync(scenario.Actor, CreateRequest(), CancellationToken.None);

        Assert.Equal(CaseRegistrationStatus.Accepted, result.Status);
        Assert.NotNull(result.CaseId);
        Assert.Equal(2, result.AggregateVersion);
        Assert.Equal(1, result.ReportRevision);
        var initialAssignment = Assert.IsType<CaseAssigned>(scenario.Persistence.AppendRequest!.Events[1]);
        Assert.Equal(scenario.Actor.ActorId, initialAssignment.AccountableOwnerId);
        Assert.Equal(scenario.Actor.ActorId, initialAssignment.ActorId);
        Assert.Single(scenario.UnitOfWork.CreateLeases);
        Assert.All(scenario.UnitOfWork.CreateLeases, lease => Assert.True(lease.Committed));
        Assert.Equal(1, scenario.Authorization.NewCalls);
    }

    [Fact]
    public async Task Comparable_contradictory_animal_counts_are_rejected_before_opening_a_lease()
    {
        var scenario = new Scenario();

        var result = await scenario.Processor.RegisterAsync(
            scenario.Actor,
            CreateAnimalRequestWithContradictoryCounts(),
            CancellationToken.None);

        Assert.Equal(CaseRegistrationStatus.Invalid, result.Status);
        Assert.Null(result.CaseId);
        Assert.Empty(scenario.UnitOfWork.CreateLeases);
        Assert.Empty(scenario.UnitOfWork.ReplayLeases);
        Assert.Equal(0, scenario.Authorization.NewCalls);
        Assert.Equal(0, scenario.Persistence.CandidateLookupCalls);
        Assert.Equal(0, scenario.Persistence.GetDatabaseClockCalls);
        Assert.Equal(0, scenario.Persistence.AppendCalls);
        Assert.Empty(scenario.Persistence.DenialAudits);
        Assert.Null(scenario.Persistence.AppendRequest);
    }

    [Fact]
    public async Task Unknown_animal_counts_are_accepted_by_managed_registration()
    {
        var scenario = new Scenario();
        PrepareAcceptedRegistration(scenario);

        var result = await scenario.Processor.RegisterAsync(
            scenario.Actor,
            CreateAnimalRequest(
                AnimalCount.Unknown(AnimalObservationSource()),
                AnimalCount.NotRecorded(AnimalObservationSource()),
                AnimalCount.Unknown(AnimalObservationSource())),
            CancellationToken.None);

        Assert.Equal(CaseRegistrationStatus.Accepted, result.Status);
        Assert.NotNull(scenario.Persistence.AppendRequest);
        Assert.Equal(1, scenario.Persistence.AppendCalls);
        Assert.Single(scenario.UnitOfWork.CreateLeases);
    }

    [Fact]
    public async Task Animal_counts_for_different_populations_or_periods_are_accepted_by_managed_registration()
    {
        var scenario = new Scenario();
        PrepareAcceptedRegistration(scenario);
        var source = AnimalObservationSource();
        var firstPeriod = ObservationPeriod(new DateOnly(2026, 10, 20), source);
        var secondPeriod = ObservationPeriod(new DateOnly(2026, 10, 21), source);

        var result = await scenario.Processor.RegisterAsync(
            scenario.Actor,
            CreateAnimalRequest(
                AnimalCount.Known(2, source, "herd:one", firstPeriod),
                AnimalCount.Known(8, source, "herd:two", firstPeriod),
                AnimalCount.Known(9, source, "herd:two", secondPeriod)),
            CancellationToken.None);

        Assert.Equal(CaseRegistrationStatus.Accepted, result.Status);
        Assert.NotNull(scenario.Persistence.AppendRequest);
        Assert.Equal(1, scenario.Persistence.AppendCalls);
        Assert.Single(scenario.UnitOfWork.CreateLeases);
    }

    [Fact]
    public async Task New_scope_denial_precedes_candidate_receipt_lookup()
    {
        var scenario = new Scenario();
        scenario.Authorization.NewDecisions.Enqueue(new CaseRegistrationAuthorizationDecision(
            false,
            AccessDecisionCode.WorkspaceOutsideGrant,
            null));

        var result = await scenario.Processor.RegisterAsync(scenario.Actor, CreateRequest(), CancellationToken.None);

        Assert.Equal(CaseRegistrationStatus.Denied, result.Status);
        Assert.Null(result.CaseId);
        Assert.True(result.DenialAuditDurable);
        Assert.Equal(0, scenario.Persistence.CandidateLookupCalls);
        Assert.Single(scenario.Persistence.DenialAudits);
        Assert.True(scenario.UnitOfWork.CreateLeases.Single().Committed);
    }

    [Fact]
    public async Task Existing_receipt_is_loaded_only_after_case_bound_current_authorization()
    {
        var request = CreateRequest();
        var snapshot = CanonicalCaseRegistrationHasher.CreateSnapshot(CreateActor("officer-1"), request);
        var scenario = new Scenario();
        scenario.Authorization.NewDecisions.Enqueue(Allowed());
        scenario.Authorization.ReplayDecisions.Enqueue(Allowed());
        scenario.Persistence.CandidateCaseIds.Enqueue(ExistingCaseId);
        scenario.Persistence.Receipt = new CaseRegistrationReceipt(
            "officer-1",
            CommandId,
            ExistingCaseId,
            CanonicalCaseRegistrationHasher.CommandType,
            CanonicalCaseRegistrationHasher.CurrentReceiptProtocolVersion,
            CanonicalCaseRegistrationHasher.CurrentWireCommandSchemaVersion,
            snapshot.CanonicalPayloadHash,
            ImmutableArray.Create(Guid.Parse("c16f9182-5529-4172-8ac2-e7f0414f1929"), Guid.Parse("31d1ff2e-4950-4c79-bbdb-0c8f9e765fcb")),
            2,
            1);

        var result = await scenario.Processor.RegisterAsync(scenario.Actor, request, CancellationToken.None);

        Assert.Equal(CaseRegistrationStatus.Replayed, result.Status);
        Assert.Equal(ExistingCaseId, result.CaseId);
        Assert.Equal(1, scenario.Persistence.CandidateLookupCalls);
        Assert.Equal(1, scenario.Persistence.AuthorizedReceiptLookupCalls);
        Assert.Equal(["new-auth", "candidate", "replay-auth", "receipt"], scenario.Timeline);
        Assert.False(scenario.UnitOfWork.CreateLeases.Single().Committed);
        Assert.False(scenario.UnitOfWork.ReplayLeases.Single().Committed);
        Assert.Null(scenario.Persistence.AppendRequest);
    }

    [Fact]
    public async Task Existing_case_authorization_denial_never_reads_the_full_receipt()
    {
        var scenario = new Scenario();
        scenario.Authorization.NewDecisions.Enqueue(Allowed());
        scenario.Authorization.ReplayDecisions.Enqueue(new CaseRegistrationAuthorizationDecision(
            false,
            AccessDecisionCode.AccessEpochMismatch,
            null));
        scenario.Persistence.CandidateCaseIds.Enqueue(ExistingCaseId);

        var result = await scenario.Processor.RegisterAsync(scenario.Actor, CreateRequest(), CancellationToken.None);

        Assert.Equal(CaseRegistrationStatus.Denied, result.Status);
        Assert.Null(result.CaseId);
        Assert.Equal(0, scenario.Persistence.AuthorizedReceiptLookupCalls);
        Assert.Single(scenario.Persistence.DenialAudits);
        Assert.True(scenario.UnitOfWork.ReplayLeases.Single().Committed);
        Assert.Equal(["new-auth", "candidate", "replay-auth"], scenario.Timeline);
    }

    [Fact]
    public async Task Receipt_hash_mismatch_conflicts_without_creating_or_staging()
    {
        var request = CreateRequest();
        var different = CanonicalCaseRegistrationHasher.CreateSnapshot(
            scenarioActor(),
            CreateRequest(workspaceId: "different-workspace"));
        var scenario = new Scenario();
        scenario.Authorization.NewDecisions.Enqueue(Allowed());
        scenario.Authorization.ReplayDecisions.Enqueue(Allowed());
        scenario.Persistence.CandidateCaseIds.Enqueue(ExistingCaseId);
        scenario.Persistence.Receipt = new CaseRegistrationReceipt(
            "officer-1",
            CommandId,
            ExistingCaseId,
            CanonicalCaseRegistrationHasher.CommandType,
            1,
            1,
            different.CanonicalPayloadHash,
            ImmutableArray.Create(Guid.NewGuid()),
            2,
            1);

        var result = await scenario.Processor.RegisterAsync(scenario.Actor, request, CancellationToken.None);

        Assert.Equal(CaseRegistrationStatus.Conflict, result.Status);
        Assert.Null(result.CaseId);
        Assert.Null(scenario.Persistence.AppendRequest);
        Assert.Equal(0, scenario.Persistence.GetDatabaseClockCalls);
    }

    [Fact]
    public async Task Command_key_insert_race_restarts_from_authorization_and_receipt_lookup_with_bound()
    {
        var scenario = new Scenario();
        for (var index = 0; index < 3; index++)
        {
            scenario.Authorization.NewDecisions.Enqueue(Allowed());
            scenario.Persistence.CandidateCaseIds.Enqueue(null);
        }

        scenario.Persistence.AppendStatuses.Enqueue(CaseRegistrationAppendStatus.CommandKeyConflict);
        scenario.Persistence.AppendStatuses.Enqueue(CaseRegistrationAppendStatus.CommandKeyConflict);
        scenario.Persistence.AppendStatuses.Enqueue(CaseRegistrationAppendStatus.CommandKeyConflict);

        var result = await scenario.Processor.RegisterAsync(scenario.Actor, CreateRequest(), CancellationToken.None);

        Assert.Equal(CaseRegistrationStatus.Unavailable, result.Status);
        Assert.Equal(3, scenario.UnitOfWork.CreateLeases.Count);
        Assert.Equal(3, scenario.Authorization.NewCalls);
        Assert.Equal(3, scenario.Persistence.CandidateLookupCalls);
        Assert.Equal(3, scenario.Persistence.AppendCalls);
        Assert.All(scenario.UnitOfWork.CreateLeases, lease => Assert.False(lease.Committed));
    }

    [Fact]
    public async Task Failed_final_stage_rolls_back_without_audit_only_commit()
    {
        var scenario = new Scenario();
        scenario.Authorization.NewDecisions.Enqueue(Allowed());
        scenario.Persistence.CandidateCaseIds.Enqueue(null);
        scenario.Persistence.AppendStatus = CaseRegistrationAppendStatus.AuthorizationChanged;

        var result = await scenario.Processor.RegisterAsync(scenario.Actor, CreateRequest(), CancellationToken.None);

        Assert.Equal(CaseRegistrationStatus.Denied, result.Status);
        Assert.False(result.DenialAuditDurable);
        Assert.Empty(scenario.Persistence.DenialAudits);
        Assert.False(scenario.UnitOfWork.CreateLeases.Single().Committed);
        Assert.NotNull(scenario.Persistence.AppendRequest);
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_any_lease_begins()
    {
        var scenario = new Scenario();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await scenario.Processor.RegisterAsync(scenario.Actor, CreateRequest(), cancellation.Token));

        Assert.Empty(scenario.UnitOfWork.CreateLeases);
        Assert.Empty(scenario.UnitOfWork.ReplayLeases);
    }

    private static RegisterManagedCaseRequest CreateRequest(string workspaceId = "workspace-1")
    {
        var source = new ReportProvenance(ReportSourceKind.ReporterObservation, "observation-1");
        var report = HumanReport.Create(
            Guid.Parse("b9e56881-9d31-4691-b91f-21520e729759"),
            new ReportContext(
                ReportedFact<ReportedTime>.Unknown(source),
                null,
                null,
                null,
                ReportedTimePrecision.Unknown,
                new LocationContext(
                    "SZ",
                    new OptionalPlace(null, null, PresenceState.Unknown),
                    new OptionalPlace(null, null, PresenceState.Unknown),
                    new OptionalPlace(null, null, PresenceState.Unknown),
                    null,
                    null,
                    null,
                    LocationSource.Reporter,
                    LocationPrecision.Unknown,
                    null)),
            ReportedFact<string>.Unknown(source),
            ReportedFact<int>.Unknown(source),
            ReportedFact<string>.Unknown(source),
            new ReportEvidence(new ReportedText("A concern was reported.", new ReportProvenance(ReportSourceKind.ReporterNarrative, "narrative-1")), [], null));
        var confirmations = new ReportConfirmations(true, "SZ", "catalogue-1", true, true, "policy-1", "v1", null, null, null);
        return new RegisterManagedCaseRequest(CommandId, workspaceId, new CaseReportEventData.HumanV2(report, confirmations));
    }

    private static ReportProvenance AnimalObservationSource() =>
        new(ReportSourceKind.ReporterObservation, "animal-observation-1");

    private static ReportedFact<ReportedTime> ObservationPeriod(DateOnly date, ReportProvenance source) =>
        ReportedFact<ReportedTime>.Known(
            new ReportedTime(null, date, null, null, null, null, ReportedTimePrecision.DateOnly),
            source);

    private static RegisterManagedCaseRequest CreateAnimalRequestWithContradictoryCounts()
    {
        var source = AnimalObservationSource();
        var period = ObservationPeriod(new DateOnly(2026, 10, 20), source);
        var population = "herd:synthetic-1";
        return CreateAnimalRequest(
            AnimalCount.Known(10, source, population, period),
            AnimalCount.Known(3, source, population, period),
            AnimalCount.Known(4, source, population, period));
    }

    private static RegisterManagedCaseRequest CreateAnimalRequest(
        AnimalCount animalsAtRisk,
        AnimalCount animalsAffected,
        AnimalCount animalsDead)
    {
        var source = AnimalObservationSource();
        var location = new LocationContext(
            "SZ",
            new OptionalPlace(null, null, PresenceState.Unknown),
            new OptionalPlace(null, null, PresenceState.Unknown),
            new OptionalPlace(null, null, PresenceState.Unknown),
            null,
            null,
            null,
            LocationSource.Reporter,
            LocationPrecision.Unknown,
            null);
        var context = new ReportContext(
            ReportedFact<ReportedTime>.Unknown(source),
            null,
            null,
            null,
            ReportedTimePrecision.Unknown,
            location);
        var report = AnimalReport.Create(
            Guid.Parse("5b1eb91e-a10d-4971-8753-f05a5a858b24"),
            context,
            AnimalSubjectScope.Herd,
            ReportedFact<string>.Known("cattle", source),
            ReportedFact<string>.Known("holding:synthetic-1", source),
            new ReportEvidence(null, [new ReportedText("Several animals were observed.", source)], null),
            animalsAtRisk,
            animalsAffected,
            animalsDead);
        var confirmations = new ReportConfirmations(
            true,
            "SZ",
            "catalogue-1",
            true,
            true,
            "policy-1",
            "v1",
            null,
            null,
            null);

        return new RegisterManagedCaseRequest(
            CommandId,
            "workspace-1",
            new CaseReportEventData.AnimalV2(report, confirmations));
    }

    private static void PrepareAcceptedRegistration(Scenario scenario)
    {
        scenario.Authorization.NewDecisions.Enqueue(Allowed());
        scenario.Authorization.NewDecisions.Enqueue(Allowed());
        scenario.Persistence.CandidateCaseIds.Enqueue(null);
        scenario.Persistence.AppendStatus = CaseRegistrationAppendStatus.Staged;
    }

    private static ActorContext scenarioActor() => CreateActor("officer-1");

    private static ActorContext CreateActor(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", actorId),
            new Claim(ActorContext.AccessEpochClaimType, "1")
        ], "test")));

    private static CaseRegistrationAuthorizationDecision Allowed() =>
        new(true, AccessDecisionCode.Allowed, "group-1");

    private sealed class Scenario
    {
        public ActorContext Actor { get; } = CreateActor("officer-1");
        public List<string> Timeline { get; } = [];
        public FakeUnitOfWork UnitOfWork { get; }
        public FakeAuthorization Authorization { get; }
        public FakePersistence Persistence { get; }
        public CaseRegistrationProcessor Processor { get; }

        public Scenario()
        {
            UnitOfWork = new FakeUnitOfWork(Actor.ActorId);
            Authorization = new FakeAuthorization(Timeline);
            Persistence = new FakePersistence(Timeline);
            Processor = new CaseRegistrationProcessor(UnitOfWork, Authorization, Persistence);
        }
    }

    private sealed class FakeUnitOfWork(string actorId) : ICaseRegistrationUnitOfWork
    {
        public List<FakeLease> CreateLeases { get; } = [];
        public List<FakeLease> ReplayLeases { get; } = [];

        public ValueTask<ICaseRegistrationLease> BeginCreateAsync(ActorContext actor, string workspaceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = new FakeLease(null, CaseRegistrationLeaseMode.Create, [actorId]);
            CreateLeases.Add(lease);
            return ValueTask.FromResult<ICaseRegistrationLease>(lease);
        }

        public ValueTask<ICaseRegistrationLease> BeginExistingCaseReplayAsync(ActorContext actor, Guid candidateCaseId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = new FakeLease(candidateCaseId, CaseRegistrationLeaseMode.ExistingCaseReplay, [actorId]);
            ReplayLeases.Add(lease);
            return ValueTask.FromResult<ICaseRegistrationLease>(lease);
        }
    }

    private sealed class FakeLease(Guid? caseId, CaseRegistrationLeaseMode mode, ImmutableArray<string> lockedActorIds) : ICaseRegistrationLease
    {
        public Guid? CaseId { get; } = caseId;
        public CaseRegistrationLeaseMode Mode { get; } = mode;
        public ImmutableArray<string> LockedActorIds { get; } = lockedActorIds;
        public bool IsActive { get; private set; } = true;
        public bool Committed { get; private set; }

        public ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Committed = true;
            IsActive = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsActive = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAuthorization(List<string> timeline) : ICaseRegistrationAuthorization
    {
        public Queue<CaseRegistrationAuthorizationDecision> NewDecisions { get; } = new();
        public Queue<CaseRegistrationAuthorizationDecision> ReplayDecisions { get; } = new();
        public int NewCalls { get; private set; }

        public ValueTask<CaseRegistrationAuthorizationDecision> AuthorizeNewScopeAsync(ActorContext actor, ICaseRegistrationLease lease, RegisterManagedCaseRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NewCalls++;
            timeline.Add("new-auth");
            return ValueTask.FromResult(NewDecisions.Dequeue());
        }

        public ValueTask<CaseRegistrationAuthorizationDecision> AuthorizeExistingCaseReplayAsync(ActorContext actor, ICaseRegistrationLease lease, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeline.Add("replay-auth");
            return ValueTask.FromResult(ReplayDecisions.Dequeue());
        }
    }

    private sealed class FakePersistence(List<string> timeline) : ICaseRegistrationPersistence
    {
        public Queue<Guid?> CandidateCaseIds { get; } = new();
        public Queue<CaseRegistrationAppendStatus> AppendStatuses { get; } = new();
        public CaseRegistrationAppendStatus AppendStatus { get; set; }
        public CaseRegistrationReceipt? Receipt { get; set; }
        public CaseRegistrationAppendRequest? AppendRequest { get; private set; }
        public List<CaseRegistrationDenialAudit> DenialAudits { get; } = [];
        public int CandidateLookupCalls { get; private set; }
        public int AuthorizedReceiptLookupCalls { get; private set; }
        public int GetDatabaseClockCalls { get; private set; }
        public int AppendCalls { get; private set; }

        public ValueTask<Guid?> FindReceiptCandidateCaseIdAsync(ICaseRegistrationLease createLease, string actorId, Guid commandId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CandidateLookupCalls++;
            timeline.Add("candidate");
            return ValueTask.FromResult(CandidateCaseIds.Dequeue());
        }

        public ValueTask<CaseRegistrationReceipt?> FindAuthorizedReceiptAsync(ICaseRegistrationLease replayLease, string actorId, Guid commandId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthorizedReceiptLookupCalls++;
            timeline.Add("receipt");
            return ValueTask.FromResult(Receipt);
        }

        public ValueTask<DateTimeOffset> GetDatabaseUtcNowAsync(ICaseRegistrationLease createLease, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetDatabaseClockCalls++;
            return ValueTask.FromResult(DatabaseNow);
        }

        public ValueTask StageDeniedAuditAsync(ICaseRegistrationLease lease, CaseRegistrationDenialAudit audit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DenialAudits.Add(audit);
            return ValueTask.CompletedTask;
        }

        public ValueTask<CaseRegistrationAppendOutcome> StageAcceptedRegistrationAsync(ICaseRegistrationLease createLease, CaseRegistrationAppendRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendCalls++;
            AppendRequest = request;
            var status = AppendStatuses.Count == 0 ? AppendStatus : AppendStatuses.Dequeue();
            var ids = status == CaseRegistrationAppendStatus.Staged
                ? request.Events.Select(item => item.EventId).ToImmutableArray()
                : ImmutableArray<Guid>.Empty;
            return ValueTask.FromResult(new CaseRegistrationAppendOutcome(status, ids, 2, 1));
        }
    }
}
