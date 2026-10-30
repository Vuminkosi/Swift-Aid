using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests;

/// <summary>
/// Case summary: per-case ReadCase enforcement, denied mapping, and field projection.
/// </summary>
public sealed class CaseSummaryQueryTests
{
    private static readonly Guid CaseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Created =
        new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AllowedCase_ReturnsSummaryData()
    {
        var query = new CaseSummaryQuery(
            new StubStore(Data()),
            new AllowPolicy(),
            new StubUnitOfWork());

        var result = await query.GetAsync(CreateOfficer(), CaseId, CancellationToken.None);

        Assert.Equal(CaseSummaryAccess.Allowed, result.Access);
        Assert.NotNull(result.Data);
        Assert.Equal(CaseId, result.Data.CaseId);
        Assert.Equal(CaseKind.Human, result.Data.Kind);
        Assert.Equal(Sector.HumanHealth, result.Data.Sector);
        Assert.Equal(ClinicalClassification.Confirmed, result.Data.Classification);
        Assert.Equal(CaseWorkflow.Active, result.Data.Workflow);
        Assert.Equal("ZW", result.Data.CountryCode);
        Assert.Equal(["ZW-HA"], result.Data.RegionIds);
        Assert.Equal("Officer One", result.Data.OwnerDisplayName);
    }

    [Fact]
    public async Task DeniedCase_MapsToDenied()
    {
        var query = new CaseSummaryQuery(
            new StubStore(Data()),
            new DenyPolicy(),
            new StubUnitOfWork());

        var result = await query.GetAsync(CreateOfficer(), CaseId, CancellationToken.None);

        Assert.Equal(CaseSummaryAccess.Denied, result.Access);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task MissingCase_MapsToDenied()
    {
        var query = new CaseSummaryQuery(
            new StubStore(null),
            new AllowPolicy(),
            new StubUnitOfWork());

        var result = await query.GetAsync(CreateOfficer(), CaseId, CancellationToken.None);

        Assert.Equal(CaseSummaryAccess.Denied, result.Access);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task StoreFailure_MapsToUnavailable()
    {
        var query = new CaseSummaryQuery(
            new ThrowingStore(),
            new AllowPolicy(),
            new StubUnitOfWork());

        var result = await query.GetAsync(CreateOfficer(), CaseId, CancellationToken.None);

        Assert.Equal(CaseSummaryAccess.Unavailable, result.Access);
    }

    [Fact]
    public async Task EmptyCaseId_IsRejected()
    {
        var query = new CaseSummaryQuery(
            new StubStore(Data()),
            new AllowPolicy(),
            new StubUnitOfWork());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            query.GetAsync(CreateOfficer(), Guid.Empty, CancellationToken.None));
    }

    private static CaseSummaryData Data() => new(
        CaseId,
        CaseKind.Human,
        Sector.HumanHealth,
        ClinicalClassification.Confirmed,
        CaseWorkflow.Active,
        "ZW",
        ["ZW-HA"],
        "officer-1",
        "Officer One",
        Created,
        Created);

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

    private sealed class DenyPolicy : ICaseAccessPolicy
    {
        public ValueTask<AccessDecision> AuthorizeAsync(
            ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(false, AccessDecisionCode.GrantMissing, null));

        public ValueTask<AccessDecision> AuthorizeAllAsync(
            ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> requiredOperations,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(false, AccessDecisionCode.GrantMissing, null));
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
        public ValueTask<ICaseCommandLease> BeginCaseAsync(
            ActorContext actor,
            Guid caseId,
            ImmutableArray<string> additionalActorHints,
            CaseLeaseMode mode,
            CancellationToken ct) =>
            ValueTask.FromResult<ICaseCommandLease>(new StubLease());
    }

    private sealed class StubStore(CaseSummaryData? data) : ICaseSummaryStore
    {
        public Task<CaseSummaryData?> GetAsync(Guid caseId, CancellationToken ct) => Task.FromResult(data);
    }

    private sealed class ThrowingStore : ICaseSummaryStore
    {
        public Task<CaseSummaryData?> GetAsync(Guid caseId, CancellationToken ct) =>
            throw new InvalidOperationException("The summary store is unavailable.");
    }
}
