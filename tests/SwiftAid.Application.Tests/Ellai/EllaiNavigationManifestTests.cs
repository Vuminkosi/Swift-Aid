using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiNavigationManifestTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("nav-cases", true)]
    [InlineData("nav-report", true)]
    [InlineData("https://example.com", false)]
    [InlineData("/my-cases", false)]
    [InlineData("nav-admin", false)]
    public void RouteEvidenceIsOptionalAndRestrictedToRegisteredDestinations(string? navigationId, bool valid)
    {
        Assert.Equal(valid, new UiObservationReport("view", "context", "web", [], [], navigationId).IsValid());
    }

    [Fact]
    public void NavigationRequiresBothFeatureAndCapability()
    {
        var registry = new EllaiUiRegistry();
        foreach (var control in EllaiNavigationManifest.Controls) registry.Register(control);
        var query = new UiRegistryQuery(EllaiNavigationManifest.FeatureId, ["system-tour"], 20);
        HashSet<string> features = [EllaiNavigationManifest.FeatureId];
        HashSet<string> capabilities = [EllaiNavigationManifest.Capability];
        Assert.Empty(registry.GetSnapshot(query, features, new HashSet<string>()).Descriptors);
        Assert.Empty(registry.GetSnapshot(query, new HashSet<string>(), capabilities).Descriptors);
        var allowed = registry.GetSnapshot(query, features, capabilities);
        Assert.Equal(EllaiNavigationManifest.Destinations.Count, allowed.Descriptors.Count);
        Assert.False(allowed.Truncated);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("/my-cases")]
    [InlineData("nav-unknown")]
    [InlineData("nav-cases?caseId=other-owner")]
    [InlineData("nav-cases/../../admin")]
    public void RouteResolutionRejectsUnregisteredTargets(string target) =>
        Assert.Null(EllaiNavigationManifest.ResolveRoute(target));

    [Fact]
    public void DestinationsAreUniqueAndUseOnlyRegisteredApplicationRoutes()
    {
        Assert.Equal("/", EllaiNavigationManifest.ResolveRoute("nav-home"));
        var destinations = EllaiNavigationManifest.Destinations;
        Assert.Equal(destinations.Count, destinations.Select(item => item.ControlId).Distinct().Count());
        Assert.Equal(destinations.Count, destinations.Select(item => item.Anchor).Distinct().Count());
        Assert.Equal(destinations.Count, destinations.Select(item => item.NavigationId).Distinct().Count());
        foreach (var destination in destinations)
        {
            Assert.StartsWith("/", destination.Route);
            Assert.DoesNotContain("//", destination.Route);
            Assert.Equal(destination.Route, EllaiNavigationManifest.ResolveRoute(destination.NavigationId));
        }
    }
}
