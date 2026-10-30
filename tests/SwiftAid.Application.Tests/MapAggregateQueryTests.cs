using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Intake;
using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Application.Tests;

/// <summary>
/// Officer-scope surveillance aggregates: per-case ReadCase enforcement,
/// small-count suppression, window/region filtering, and country labels.
/// </summary>
public sealed class MapAggregateQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OwnedCases_GroupedByCountryAndRegion_WithCountryLabels()
    {
        var store = new StubStore(
        [
            Row("SZ", ["SZ-HH"], Sector.HumanHealth, Now.AddDays(-2)),
            Row("SZ", ["SZ-HH"], Sector.HumanHealth, Now.AddDays(-3)),
            Row("SZ", ["SZ-HH"], Sector.HumanHealth, Now.AddDays(-4)),
            Row("SZ", ["SZ-HH"], Sector.HumanHealth, Now.AddDays(-5)),
            Row("SZ", ["SZ-HH"], Sector.HumanHealth, Now.AddDays(-6)),
            Row("SZ", ["SZ-SH"], Sector.AnimalHealth, Now.AddDays(-2)),
            Row("SZ", ["SZ-SH"], Sector.AnimalHealth, Now.AddDays(-3)),
            Row("SZ", ["SZ-SH"], Sector.AnimalHealth, Now.AddDays(-4)),
            Row("SZ", ["SZ-SH"], Sector.AnimalHealth, Now.AddDays(-5)),
            Row("SZ", ["SZ-SH"], Sector.AnimalHealth, Now.AddDays(-6)),
            Row("ZW", [], Sector.HumanHealth, Now.AddDays(-2)),
            Row("ZW", [], Sector.HumanHealth, Now.AddDays(-3)),
            Row("ZW", [], Sector.HumanHealth, Now.AddDays(-4)),
            Row("ZW", [], Sector.HumanHealth, Now.AddDays(-5)),
            Row("ZW", [], Sector.HumanHealth, Now.AddDays(-6))
        ]);
        var query = new SurveillanceMapAggregateQuery(
            store, new AllowPolicy(), new StubUnitOfWork(), new StubMetadata(), new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), null, TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(SurveillanceAggregateAccess.Allowed, result.Access);
        Assert.False(result.Truncated);
        var eSwatini = Assert.Single(result.Areas, area =>
            area.Precision == SurveillanceAggregatePrecision.Country && area.AreaCode == "SZ");
        Assert.Equal("Eswatini", eSwatini.AreaLabel);
        Assert.False(eSwatini.IsSuppressed);
        Assert.Equal(5, eSwatini.HumanCount);
        Assert.Equal(5, eSwatini.AnimalCount);
        Assert.Equal(10, eSwatini.Denominator);
        var hhohho = Assert.Single(result.Areas, area =>
            area.Precision == SurveillanceAggregatePrecision.Region && area.AreaCode == "SZ-HH");
        Assert.False(hhohho.IsSuppressed);
        Assert.Equal(5, hhohho.HumanCount);
        var zimbabwe = Assert.Single(result.Areas, area =>
            area.Precision == SurveillanceAggregatePrecision.Country && area.AreaCode == "ZW");
        Assert.Equal("ZW", zimbabwe.AreaLabel);
        Assert.Equal(5, zimbabwe.HumanCount);
    }

    [Fact]
    public async Task SmallGroups_AreSuppressedWithoutCounts()
    {
        var store = new StubStore(
        [
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-1)),
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-2))
        ]);
        var query = new SurveillanceMapAggregateQuery(
            store, new AllowPolicy(), new StubUnitOfWork(), new StubMetadata(), new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), null, TimeSpan.FromDays(30), CancellationToken.None);

        var area = Assert.Single(result.Areas);
        Assert.True(area.IsSuppressed);
        Assert.Null(area.HumanCount);
        Assert.Null(area.AnimalCount);
        Assert.Null(area.Denominator);
        Assert.False(string.IsNullOrWhiteSpace(area.SuppressionReason));
        Assert.True(area.IsValid);
    }

    [Fact]
    public async Task DeniedCases_AreSkippedWithoutLease()
    {
        var readable = Guid.NewGuid();
        var denied = Guid.NewGuid();
        var store = new StubStore(
        [
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-1), readable),
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-1), denied)
        ]);
        var unitOfWork = new StubUnitOfWork();
        var query = new SurveillanceMapAggregateQuery(
            store, new ScopedPolicy(denied), unitOfWork, new StubMetadata(), new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), null, TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(SurveillanceAggregateAccess.Allowed, result.Access);
        Assert.Equal(2, unitOfWork.BeginCalls);
        // One readable case is below the suppression threshold, so the row is withheld.
        var area = Assert.Single(result.Areas);
        Assert.True(area.IsSuppressed);
    }

    [Fact]
    public async Task RegionFilter_LimitsToMatchingCountryAndRegion()
    {
        var store = new StubStore(
        [
            Row("SZ", ["SZ-HH"], Sector.HumanHealth, Now.AddDays(-1)),
            Row("SZ", ["SZ-HH"], Sector.HumanHealth, Now.AddDays(-2)),
            Row("SZ", ["SZ-HH"], Sector.HumanHealth, Now.AddDays(-3)),
            Row("SZ", ["SZ-HH"], Sector.HumanHealth, Now.AddDays(-4)),
            Row("SZ", ["SZ-HH"], Sector.HumanHealth, Now.AddDays(-5)),
            Row("ZW", [], Sector.HumanHealth, Now.AddDays(-1)),
            Row("ZW", [], Sector.HumanHealth, Now.AddDays(-2)),
            Row("ZW", [], Sector.HumanHealth, Now.AddDays(-3)),
            Row("ZW", [], Sector.HumanHealth, Now.AddDays(-4)),
            Row("ZW", [], Sector.HumanHealth, Now.AddDays(-5))
        ]);
        var query = new SurveillanceMapAggregateQuery(
            store, new AllowPolicy(), new StubUnitOfWork(), new StubMetadata(), new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), "SZ-HH", TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(2, result.Areas.Count);
        Assert.Contains(result.Areas, area =>
            area.Precision == SurveillanceAggregatePrecision.Country && area.AreaCode == "SZ");
        Assert.Contains(result.Areas, area =>
            area.Precision == SurveillanceAggregatePrecision.Region && area.AreaCode == "SZ-HH");
        Assert.DoesNotContain(result.Areas, area => area.AreaCode == "ZW");
    }

    [Fact]
    public async Task WindowFilter_ExcludesOlderRecords()
    {
        var store = new StubStore(
        [
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-2)),
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-2)),
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-2)),
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-2)),
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-2)),
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-200)),
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-200)),
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-200)),
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-200)),
            Row("SZ", [], Sector.HumanHealth, Now.AddDays(-200))
        ]);
        var query = new SurveillanceMapAggregateQuery(
            store, new AllowPolicy(), new StubUnitOfWork(), new ThrowingMetadata(), new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), null, TimeSpan.FromDays(30), CancellationToken.None);

        var area = Assert.Single(result.Areas);
        Assert.False(area.IsSuppressed);
        Assert.Equal(5, area.HumanCount);
        // Metadata failure falls back to the area code as the label.
        Assert.Equal("SZ", area.AreaLabel);
    }

    [Fact]
    public async Task StoreFailure_ReturnsUnavailable()
    {
        var query = new SurveillanceMapAggregateQuery(
            new ThrowingStore(), new AllowPolicy(), new StubUnitOfWork(),
            new StubMetadata(), new FixedTimeProvider(Now));

        var result = await query.GetAsync(CreateOfficer(), null, TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(SurveillanceAggregateAccess.Unavailable, result.Access);
        Assert.Empty(result.Areas);
    }

    [Theory]
    [InlineData("Z")]
    [InlineData("SZ!")]
    public async Task InvalidRegionCode_IsRejected(string regionCode)
    {
        var query = new SurveillanceMapAggregateQuery(
            new StubStore([]), new AllowPolicy(), new StubUnitOfWork(),
            new StubMetadata(), new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            query.GetAsync(CreateOfficer(), regionCode, TimeSpan.FromDays(30), CancellationToken.None));
    }

    [Fact]
    public async Task ParallelScopes_AuthorizeAllCandidatesWithAtMostEightLanes()
    {
        var rows = Enumerable.Range(0, 20)
            .Select(_ => Row("SZ", [], Sector.HumanHealth, Now.AddDays(-2)))
            .ToList();
        var factory = new CountingScopeFactory();
        var query = new SurveillanceMapAggregateQuery(
            new StubStore(rows), new AllowPolicy(), new StubUnitOfWork(),
            new StubMetadata(), new FixedTimeProvider(Now), factory);

        var result = await query.GetAsync(CreateOfficer(), null, TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(SurveillanceAggregateAccess.Allowed, result.Access);
        Assert.Equal(20, result.Areas.Sum(area => area.Denominator ?? 0));
        Assert.True(factory.ScopesCreated <= SurveillanceMapAggregateQuery.MaxAuthorizationParallelism,
            $"Lanes used: {factory.ScopesCreated}");
        Assert.Equal(20, factory.TotalBeginCalls);
    }

    [Fact]
    public async Task OutOfRangeWindow_IsRejected()
    {
        var query = new SurveillanceMapAggregateQuery(
            new StubStore([]), new AllowPolicy(), new StubUnitOfWork(),
            new StubMetadata(), new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            query.GetAsync(CreateOfficer(), null, TimeSpan.FromDays(400), CancellationToken.None));
    }

    private static MapAggregateCaseRow Row(
        string country, string[] regions, Sector sector, DateTimeOffset createdAt, Guid? caseId = null) =>
        new(caseId ?? Guid.NewGuid(), country, regions, sector, createdAt);

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

    private sealed class CountingScopeFactory : IEllaiCaseAuthScopeFactory
    {
        public int ScopesCreated;

        public int TotalBeginCalls;

        public IEllaiCaseAuthScope CreateScope()
        {
            Interlocked.Increment(ref ScopesCreated);
            return new CountingScope(this);
        }

        private sealed class CountingScope(CountingScopeFactory parent) : IEllaiCaseAuthScope
        {
            private readonly StubUnitOfWork _unitOfWork = new();

            public ICaseCommandUnitOfWork UnitOfWork => _unitOfWork;

            public ICaseAccessPolicy Policy => new AllowPolicy();

            public ValueTask DisposeAsync()
            {
                Interlocked.Add(ref parent.TotalBeginCalls, _unitOfWork.BeginCalls);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class StubStore(IReadOnlyList<MapAggregateCaseRow> rows) : IMapAggregateStore
    {
        public Task<IReadOnlyList<MapAggregateCaseRow>> GetOwnedCaseAreasAsync(
            string ownerActorId, DateTimeOffset sinceUtc, int take, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MapAggregateCaseRow>>(
                rows.Where(row => row.CreatedAtUtc >= sinceUtc).Take(take).ToArray());
    }

    private sealed class ThrowingStore : IMapAggregateStore
    {
        public Task<IReadOnlyList<MapAggregateCaseRow>> GetOwnedCaseAreasAsync(
            string ownerActorId, DateTimeOffset sinceUtc, int take, CancellationToken ct) =>
            throw new InvalidOperationException("The aggregate store is unavailable.");
    }

    private sealed class StubMetadata : IIntakePublicMetadataProvider
    {
        public PublicIntakeMetadata GetMetadata() => new("test", "test", "test", "test", "test");

        public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken ct) =>
            Task.FromResult(new PublicIntakeCountryCatalogue(
                "test",
                "test",
                [new PublicIntakeCountry("SZ", "Eswatini")]));
    }

    private sealed class ThrowingMetadata : IIntakePublicMetadataProvider
    {
        public PublicIntakeMetadata GetMetadata() => new("test", "test", "test", "test", "test");

        public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken ct) =>
            throw new InvalidOperationException("The catalogue is unavailable.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
