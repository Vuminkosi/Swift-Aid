using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests.Cases;

public sealed class CaseCorrectionReadQueryTests
{
    private static readonly Guid CaseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReportReadRequiresReadGrantBeforeDecryptingEvidence()
    {
        var persistence = new StubPersistence(TypedAggregate());
        var query = new CaseReportReadQuery(new StubPolicy(false, false), new StubUnitOfWork(), persistence);
        Assert.Null(await query.GetAsync(Actor(), CaseId, CancellationToken.None));
        Assert.Equal(0, persistence.ReplayCalls);
    }

    [Fact]
    public async Task ReportReadShowsTypedEvidenceWithoutGrantingCorrection()
    {
        var policy = new StubPolicy(true, false);
        var query = new CaseReportReadQuery(policy, new StubUnitOfWork(), new StubPersistence(TypedAggregate()));
        var result = await query.GetAsync(Actor(), CaseId, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("synthetic typed narrative", result.Narrative);
        Assert.Equal("reported symptom, not a diagnosis", Assert.Single(result.Observations));
        Assert.Equal("ZA", result.Location?.CountryCode);
        Assert.Equal([CaseOperation.ReadCase], policy.Operations);
        Assert.Empty(policy.CombinedOperations);
    }

    [Fact]
    public async Task ReportReadDoesNotTurnStorageFailureIntoEmptyObservations()
    {
        var query = new CaseReportReadQuery(new StubPolicy(true, false), new StubUnitOfWork(), new StubPersistence(null, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => query.GetAsync(Actor(), CaseId, CancellationToken.None));
    }

    [Fact]
    public async Task LegacyReport_WithBothGrants_ReturnsVersionAndNarrative()
    {
        var policy = new StubPolicy(true, true);
        var query = CreateQuery(policy, LegacyAggregate());

        var result = await query.GetAsync(Actor(), CaseId, CancellationToken.None);

        Assert.Equal(CaseCorrectionReadAccess.Allowed, result.Access);
        Assert.Equal(new CaseCorrectionReadData(CaseId, 2, 1, CaseCorrectionReportKind.LegacyV1, true, "synthetic narrative"), result.Data);
        Assert.Equal([CaseOperation.ReadCase], policy.Operations);
        Assert.Equal([CaseOperation.ReadCase, CaseOperation.CorrectReport], Assert.Single(policy.CombinedOperations));
    }

    [Fact]
    public async Task ReadDenied_ReturnsNotFoundAndDoesNotReplayReport()
    {
        var persistence = new StubPersistence(LegacyAggregate());
        var query = new CaseCorrectionReadQuery(new StubPolicy(false, true), new StubUnitOfWork(), persistence);

        var result = await query.GetAsync(Actor(), CaseId, CancellationToken.None);

        Assert.Equal(CaseCorrectionReadAccess.NotFoundOrNotAuthorized, result.Access);
        Assert.Null(result.Data);
        Assert.Equal(0, persistence.ReplayCalls);
    }

    [Fact]
    public async Task CorrectionDenied_ReturnsNotFoundWithoutData()
    {
        var query = CreateQuery(new StubPolicy(true, false), LegacyAggregate());

        var result = await query.GetAsync(Actor(), CaseId, CancellationToken.None);

        Assert.Equal(CaseCorrectionReadAccess.NotFoundOrNotAuthorized, result.Access);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task TypedReport_NeverReturnsNarrativeOrCorrectionCapability()
    {
        var query = CreateQuery(new StubPolicy(true, true), TypedAggregate());

        var result = await query.GetAsync(Actor(), CaseId, CancellationToken.None);

        Assert.Equal(CaseCorrectionReadAccess.Allowed, result.Access);
        Assert.NotNull(result.Data);
        Assert.Equal(CaseCorrectionReportKind.HumanV2, result.Data.ReportKind);
        Assert.False(result.Data.CanCorrectReport);
        Assert.Null(result.Data.Narrative);
    }

    [Fact]
    public async Task MissingCase_MapsToNotFoundOrNotAuthorized()
    {
        var query = new CaseCorrectionReadQuery(new StubPolicy(true, true), new StubUnitOfWork(missing: true), new StubPersistence(LegacyAggregate()));

        var result = await query.GetAsync(Actor(), CaseId, CancellationToken.None);

        Assert.Equal(CaseCorrectionReadAccess.NotFoundOrNotAuthorized, result.Access);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task PersistenceFailure_ReturnsUnavailableWithoutData()
    {
        var query = new CaseCorrectionReadQuery(new StubPolicy(true, true), new StubUnitOfWork(), new StubPersistence(null, throwOnReplay: true));

        var result = await query.GetAsync(Actor(), CaseId, CancellationToken.None);

        Assert.Equal(CaseCorrectionReadAccess.Unavailable, result.Access);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task AuthorizationFailure_ReturnsUnavailableWithoutData()
    {
        var query = new CaseCorrectionReadQuery(new StubPolicy(true, true, throwOnCorrection: true), new StubUnitOfWork(), new StubPersistence(LegacyAggregate()));

        var result = await query.GetAsync(Actor(), CaseId, CancellationToken.None);

        Assert.Equal(CaseCorrectionReadAccess.Unavailable, result.Access);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task ReadAuthorizationSourceUnavailable_ReturnsUnavailableWithoutData()
    {
        var query = CreateQuery(new StubPolicy(true, true, readDecisionCode: AccessDecisionCode.ResourceSourceUnavailable), LegacyAggregate());

        var result = await query.GetAsync(Actor(), CaseId, CancellationToken.None);

        Assert.Equal(CaseCorrectionReadAccess.Unavailable, result.Access);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task CorrectionAuthorizationSourceUnavailable_ReturnsUnavailableWithoutData()
    {
        var query = CreateQuery(new StubPolicy(true, true, combinedDecisionCode: AccessDecisionCode.GrantSourceUnavailable), LegacyAggregate());

        var result = await query.GetAsync(Actor(), CaseId, CancellationToken.None);

        Assert.Equal(CaseCorrectionReadAccess.Unavailable, result.Access);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task ReadRevokedAfterReplay_DeniesJointFinalAuthorizationWithoutData()
    {
        var policy = new StubPolicy(true, true, denyCombined: true);
        var persistence = new StubPersistence(LegacyAggregate());
        var query = new CaseCorrectionReadQuery(policy, new StubUnitOfWork(), persistence);

        var result = await query.GetAsync(Actor(), CaseId, CancellationToken.None);

        Assert.Equal(CaseCorrectionReadAccess.NotFoundOrNotAuthorized, result.Access);
        Assert.Null(result.Data);
        Assert.Equal(1, persistence.ReplayCalls);
        Assert.Equal([CaseOperation.ReadCase], policy.Operations);
        Assert.Equal([CaseOperation.ReadCase, CaseOperation.CorrectReport], Assert.Single(policy.CombinedOperations));
    }

    private static CaseCorrectionReadQuery CreateQuery(StubPolicy policy, CaseAggregate aggregate) =>
        new(policy, new StubUnitOfWork(), new StubPersistence(aggregate));

    private static ActorContext Actor() => ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, "staff-actor"),
        new Claim(ActorContext.AccessEpochClaimType, "7")
    ], "unit-test")));

    private static CaseAggregate LegacyAggregate() => CaseAggregate.Replay(
        CreateLegacyEvents());

    private static IReadOnlyList<CaseEvent> CreateLegacyEvents()
    {
        var commandId = Guid.NewGuid();
        return
        [
            new CaseReported(CaseId, 1, 1, commandId, "staff-actor", null, Now,
                new ReportFacts(CaseKind.Human, Sector.HumanHealth, "synthetic narrative", [new ReportedObservation("synthetic observation")]), "workspace-a"),
            new CaseAssigned(CaseId, 2, 1, commandId, "staff-actor", null, Now, "staff-actor")
        ];
    }

    private static CaseAggregate TypedAggregate()
    {
        var provenance = new ReportProvenance(ReportSourceKind.ReporterNarrative, "synthetic-reference");
        var occurrenceProvenance = new ReportProvenance(ReportSourceKind.ReporterObservation, "synthetic-occurrence");
        var recorded = Now;
        var occurrence = new ReportedTime(Now, null, null, null, "+00:00", "Etc/UTC", ReportedTimePrecision.Exact);
        var location = new LocationContext("ZA", new OptionalPlace(null, null, PresenceState.Unknown),
            new OptionalPlace(null, null, PresenceState.Unknown), new OptionalPlace(null, null, PresenceState.Unknown),
            null, null, null, LocationSource.Reporter, LocationPrecision.Unknown, "catalogue-test");
        var context = new ReportContext(ReportedFact<ReportedTime>.Known(occurrence, occurrenceProvenance), recorded,
            "+00:00", "Etc/UTC", ReportedTimePrecision.Exact, location);
        var report = HumanReport.Create(Guid.Parse("40000000-0000-0000-0000-000000000002"), context,
            ReportedFact<string>.Unknown(provenance), ReportedFact<int>.Unknown(provenance),
            ReportedFact<string>.Known("reported concern", provenance),
            new ReportEvidence(new ReportedText("synthetic typed narrative", provenance), [new ReportedText("reported symptom, not a diagnosis", provenance)], null));
        var reportData = new CaseReportEventData.HumanV2(report,
            new ReportConfirmations(true, "ZA", "catalogue-test", true, true, "policy-test", "v1", null, null, null));
        var commandId = Guid.NewGuid();
        return CaseAggregate.Replay(
        [
            new CaseReported(CaseId, 1, 1, commandId, "staff-actor", null, Now, reportData, "workspace-a"),
            new CaseAssigned(CaseId, 2, 1, commandId, "staff-actor", null, Now, "staff-actor")
        ]);
    }

    private sealed class StubPolicy(
        bool allowRead,
        bool allowCorrection,
        bool throwOnCorrection = false,
        AccessDecisionCode? readDecisionCode = null,
        bool denyCombined = false,
        AccessDecisionCode? combinedDecisionCode = null) : ICaseAccessPolicy
    {
        public List<CaseOperation> Operations { get; } = [];
        public List<CaseOperation[]> CombinedOperations { get; } = [];

        public ValueTask<AccessDecision> AuthorizeAsync(ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add(operation);
            if (throwOnCorrection && operation == CaseOperation.CorrectReport)
                throw new InvalidOperationException("Synthetic authorization source failure.");
            var allowed = operation == CaseOperation.ReadCase ? allowRead : operation == CaseOperation.CorrectReport && allowCorrection;
            var code = readDecisionCode ?? (allowed ? AccessDecisionCode.Allowed : AccessDecisionCode.GrantMissing);
            return ValueTask.FromResult(new AccessDecision(allowed, code, null));
        }

        public ValueTask<AccessDecision> AuthorizeAllAsync(ActorContext actor, Guid caseId,
            IReadOnlyCollection<CaseOperation> requiredOperations, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CombinedOperations.Add(requiredOperations.ToArray());
            if (throwOnCorrection)
                throw new InvalidOperationException("Synthetic authorization source failure.");
            var allowed = allowRead && allowCorrection && !denyCombined;
            var code = combinedDecisionCode ?? (allowed ? AccessDecisionCode.Allowed : AccessDecisionCode.GrantMissing);
            return ValueTask.FromResult(new AccessDecision(allowed, code, null));
        }
    }

    private sealed class StubLease : ICaseCommandLease
    {
        public Guid CaseId => CaseIdValue;
        private Guid CaseIdValue => CaseCorrectionReadQueryTests.CaseId;
        public CaseLeaseMode Mode => CaseLeaseMode.Read;
        public ImmutableArray<string> LockedActorIds => [];
        public bool IsActive => true;
        public ValueTask CommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubUnitOfWork(bool missing = false) : ICaseCommandUnitOfWork
    {
        public ValueTask<ICaseCommandLease> BeginCaseAsync(ActorContext actor, Guid caseId,
            ImmutableArray<string> additionalActorHints, CaseLeaseMode mode, CancellationToken cancellationToken) =>
            missing
                ? ValueTask.FromException<ICaseCommandLease>(new KeyNotFoundException())
                : ValueTask.FromResult<ICaseCommandLease>(new StubLease());
    }

    private sealed class StubPersistence(CaseAggregate? aggregate, bool throwOnReplay = false) : ICaseCommandPersistence
    {
        public int ReplayCalls { get; private set; }

        public ValueTask<CaseAggregate> ReplayCaseAsync(ICaseCommandLease lease, CancellationToken cancellationToken)
        {
            ReplayCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (throwOnReplay) throw new InvalidOperationException("Synthetic journal failure.");
            return aggregate is null
                ? ValueTask.FromException<CaseAggregate>(new InvalidOperationException("No aggregate was provided."))
                : ValueTask.FromResult(aggregate);
        }

        public ValueTask<CaseCommandCaseHeader?> ReadCaseHeaderAsync(ICaseCommandLease lease, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CaseCommandReceipt?> FindReceiptAsync(ICaseCommandLease lease, string actorId, Guid commandId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<DateTimeOffset> GetDatabaseUtcNowAsync(ICaseCommandLease lease, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> IsEligibleAssignmentTargetAsync(ICaseCommandLease lease, string targetActorId, CaseCommandCaseHeader caseHeader, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CaseCommandAppendOutcome> StageAcceptedCommandAsync(ICaseCommandLease lease, CaseCommandAppendRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask StageDeniedAuditAsync(ICaseCommandLease lease, CaseCommandDenialAudit audit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
