using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Application.Tests;

public sealed class CoverageRouterTests
{
    [Fact]
    public async Task EligibleWithValidQueue_RoutesForReviewWithQueueId()
    {
        var router = new CoverageRouter(new ScriptedCatalog(
            new IntakeCoverageDecision(IntakeCoverageMatch.Eligible, "cape-town-north_01")));

        var result = await router.RouteAsync(Location("ZA"), CaseKind.Human, CancellationToken.None);

        Assert.Equal(IntakeRoutingStatus.RoutedForReview, result.Status);
        Assert.Equal("cape-town-north_01", result.IntakeQueueId);
    }

    [Fact]
    public async Task EligibleWithoutQueue_NeedsRoutingAndRevealsNoQueue()
    {
        var router = new CoverageRouter(new ScriptedCatalog(
            new IntakeCoverageDecision(IntakeCoverageMatch.Eligible, null)));

        var result = await router.RouteAsync(Location("ZA"), CaseKind.Animal, CancellationToken.None);

        Assert.Equal(IntakeRoutingStatus.NeedsRouting, result.Status);
        Assert.Null(result.IntakeQueueId);
    }

    [Fact]
    public async Task EligibleWithInvalidQueue_NeedsRoutingAndRevealsNoQueue()
    {
        var router = new CoverageRouter(new ScriptedCatalog(
            new IntakeCoverageDecision(IntakeCoverageMatch.Eligible, "has space")));

        var result = await router.RouteAsync(Location("ZA"), CaseKind.Human, CancellationToken.None);

        Assert.Equal(IntakeRoutingStatus.NeedsRouting, result.Status);
        Assert.Null(result.IntakeQueueId);
    }

    [Fact]
    public async Task NoMatch_ReturnsNoServiceCoverageAndNeverImpliesAssignment()
    {
        var router = new CoverageRouter(new ScriptedCatalog(
            new IntakeCoverageDecision(IntakeCoverageMatch.None, "cape-town-north_01")));

        var result = await router.RouteAsync(Location("AQ"), CaseKind.Human, CancellationToken.None);

        Assert.Equal(IntakeRoutingStatus.NoServiceCoverage, result.Status);
        Assert.Null(result.IntakeQueueId);
    }

    [Fact]
    public async Task AmbiguousMatch_NeedsRoutingAndRevealsNoQueue()
    {
        var router = new CoverageRouter(new ScriptedCatalog(
            new IntakeCoverageDecision(IntakeCoverageMatch.Ambiguous, "cape-town-north_01")));

        var result = await router.RouteAsync(Location("ZA"), CaseKind.Human, CancellationToken.None);

        Assert.Equal(IntakeRoutingStatus.NeedsRouting, result.Status);
        Assert.Null(result.IntakeQueueId);
    }

    [Fact]
    public async Task CatalogFailure_FallsBackToNeedsRoutingWithoutQueue()
    {
        var router = new CoverageRouter(new ThrowingCatalog());

        var result = await router.RouteAsync(Location("ZA"), CaseKind.Human, CancellationToken.None);

        Assert.Equal(IntakeRoutingStatus.NeedsRouting, result.Status);
        Assert.Null(result.IntakeQueueId);
    }

    [Fact]
    public async Task UndefinedKind_ThrowsWithoutConsultingCatalog()
    {
        var catalog = new ScriptedCatalog(
            new IntakeCoverageDecision(IntakeCoverageMatch.Eligible, "cape-town-north_01"));
        var router = new CoverageRouter(catalog);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => router.RouteAsync(Location("ZA"), (CaseKind)999, CancellationToken.None));
        Assert.Equal(0, catalog.Calls);
    }

    [Fact]
    public async Task NullLocation_ThrowsWithoutConsultingCatalog()
    {
        var catalog = new ScriptedCatalog(
            new IntakeCoverageDecision(IntakeCoverageMatch.Eligible, "cape-town-north_01"));
        var router = new CoverageRouter(catalog);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => router.RouteAsync(null!, CaseKind.Human, CancellationToken.None));
        Assert.Equal(0, catalog.Calls);
    }

    private static LocationContext Location(string countryCode) => new(
        countryCode,
        new OptionalPlace("Region", null, PresenceState.Known),
        new OptionalPlace("Area", null, PresenceState.Known),
        new OptionalPlace("Locality", null, PresenceState.Known),
        null,
        null,
        null,
        LocationSource.Reporter,
        LocationPrecision.Approximate,
        "2026.10.27-1");

    private sealed class ScriptedCatalog(IntakeCoverageDecision decision) : IIntakeCoverageCatalog
    {
        public int Calls { get; private set; }

        public Task<IntakeCoverageDecision> FindAsync(LocationContext location, CaseKind kind, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(decision);
        }
    }

    private sealed class ThrowingCatalog : IIntakeCoverageCatalog
    {
        public Task<IntakeCoverageDecision> FindAsync(LocationContext location, CaseKind kind, CancellationToken ct) =>
            throw new InvalidOperationException("Catalogue unavailable.");
    }
}
