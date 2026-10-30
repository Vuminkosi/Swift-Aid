using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests;

/// <summary>
/// Officer-scope dashboard aggregates: per-case ReadCase enforcement,
/// window filtering, truncation, and store-failure behavior.
/// </summary>
public sealed class DashboardAggregateQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OwnedReadableRows_AreReturnedWithRecordFields()
    {
        var caseId = Guid.NewGuid();
        var store = new StubStore(
        [
            Row(caseId, Sector.HumanHealth, ClinicalClassification.Confirmed, CaseWorkflow.Active, Now.AddDays(-2))
        ]);
        var query = new DashboardAggregateQuery(
            store, new AllowPolicy(), new StubUnitOfWork(), new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(DashboardAggregateAccess.Allowed, result.Access);
        Assert.False(result.Truncated);
        var row = Assert.Single(result.Rows);
        Assert.Equal(caseId, row.CaseId);
        Assert.Equal(Sector.HumanHealth, row.Sector);
        Assert.Equal(ClinicalClassification.Confirmed, row.Classification);
        Assert.Equal(CaseWorkflow.Active, row.Workflow);
        Assert.Equal("ZW", row.CountryCode);
        Assert.Equal(["ZW-HA"], row.RegionIds);
    }

    [Fact]
    public async Task DeniedCases_AreSkipped()
    {
        var readable = Guid.NewGuid();
        var denied = Guid.NewGuid();
        var store = new StubStore(
        [
            Row(readable, Sector.HumanHealth, ClinicalClassification.Suspected, CaseWorkflow.Reported, Now.AddDays(-1)),
            Row(denied, Sector.HumanHealth, ClinicalClassification.Suspected, CaseWorkflow.Reported, Now.AddDays(-1))
        ]);
        var unitOfWork = new StubUnitOfWork();
        var query = new DashboardAggregateQuery(
            store, new ScopedPolicy(denied), unitOfWork, new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(DashboardAggregateAccess.Allowed, result.Access);
        Assert.Equal(2, unitOfWork.BeginCalls);
        Assert.Equal(readable, Assert.Single(result.Rows).CaseId);
    }

    [Fact]
    public async Task StoreFailure_ReturnsUnavailable()
    {
        var query = new DashboardAggregateQuery(
            new ThrowingStore(), new AllowPolicy(), new StubUnitOfWork(), new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(DashboardAggregateAccess.Unavailable, result.Access);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task OversizedCandidateSet_IsTruncated()
    {
        var rows = Enumerable.Range(0, DashboardAggregateQuery.MaxCases + 5)
            .Select(_ => Row(
                Guid.NewGuid(),
                Sector.HumanHealth,
                ClinicalClassification.Unclassified,
                CaseWorkflow.Reported,
                Now.AddDays(-1)))
            .ToList();
        var query = new DashboardAggregateQuery(
            new StubStore(rows), new AllowPolicy(), new StubUnitOfWork(), new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(DashboardAggregateAccess.Allowed, result.Access);
        Assert.True(result.Truncated);
        Assert.Equal(DashboardAggregateQuery.MaxCases, result.Rows.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    public async Task OutOfRangeWindow_IsRejected(int days)
    {
        var query = new DashboardAggregateQuery(
            new StubStore([]), new AllowPolicy(), new StubUnitOfWork(), new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            query.GetAsync(CreateOfficer(), TimeSpan.FromDays(days), CancellationToken.None));
    }

    private static DashboardAggregateCaseRow Row(
        Guid caseId,
        Sector sector,
        ClinicalClassification classification,
        CaseWorkflow workflow,
        DateTimeOffset createdAt) =>
        new(caseId, sector, classification, workflow, "ZW", ["ZW-HA"], createdAt, createdAt);

    private static ActorContext CreateOfficer() => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "officer-1"),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "unit-test")));

    private sealed class AllowPolicy : ICaseAccessPolicy
    {
        public ValueTask<AccessDecision> AuthorizeAsync(
            ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(true, AccessDecisionCode.Allowed, null));

        public ValueTask<AccessDecision> AuthorizeAllAsync(
            ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> requiredOperations,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(true, AccessDecisionCode.Allowed, null));
    }

    private sealed class ScopedPolicy(Guid deniedCaseId) : ICaseAccessPolicy
    {
        public ValueTask<AccessDecision> AuthorizeAsync(
            ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(caseId == deniedCaseId
                ? new AccessDecision(false, AccessDecisionCode.GrantMissing, null)
                : new AccessDecision(true, AccessDecisionCode.Allowed, null));

        public ValueTask<AccessDecision> AuthorizeAllAsync(
            ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> requiredOperations,
            CancellationToken cancellationToken) =>
            AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, cancellationToken);
    }

    private sealed class StubLease : ICaseCommandLease
    {
        public Guid CaseId => Guid.Empty;
        public CaseLeaseMode Mode => CaseLeaseMode.Read;
        public ImmutableArray<string> LockedActorIds => [];
        public bool IsActive => true;
        public ValueTask CommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubUnitOfWork : ICaseCommandUnitOfWork
    {
        public int BeginCalls { get; private set; }

        public ValueTask<ICaseCommandLease> BeginCaseAsync(
            ActorContext actor,
            Guid caseId,
            ImmutableArray<string> additionalActorHints,
            CaseLeaseMode mode,
            CancellationToken ct)
        {
            BeginCalls++;
            return ValueTask.FromResult<ICaseCommandLease>(new StubLease());
        }
    }

    private sealed class StubStore(IReadOnlyList<DashboardAggregateCaseRow> rows) : IDashboardAggregateStore
    {
        public Task<IReadOnlyList<DashboardAggregateCaseRow>> GetOwnedCaseRowsAsync(
            string ownerActorId, DateTimeOffset sinceUtc, int take, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DashboardAggregateCaseRow>>(
                rows.Where(row => row.CreatedAtUtc >= sinceUtc).Take(take).ToArray());
    }

    private sealed class ThrowingStore : IDashboardAggregateStore
    {
        public Task<IReadOnlyList<DashboardAggregateCaseRow>> GetOwnedCaseRowsAsync(
            string ownerActorId, DateTimeOffset sinceUtc, int take, CancellationToken ct) =>
            throw new InvalidOperationException("The aggregate store is unavailable.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
