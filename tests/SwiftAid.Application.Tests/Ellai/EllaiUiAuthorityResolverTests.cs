using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Sync;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiUiAuthorityResolverTests
{
    private static readonly ActorContext Actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(
        new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "owner"), new(ActorContext.AccessEpochClaimType, "7")], "test")));

    [Theory]
    [InlineData(7, 60, -60, 60, true)]
    [InlineData(8, 60, -60, 60, false)]
    [InlineData(7, -1, -60, 60, false)]
    [InlineData(7, 60, -60, -1, false)]
    [InlineData(7, 60, 30, 60, false)]
    public async Task NavigationRequiresCurrentEpochAndCurrentlyValidGrant(long epoch, int expiry, int starts, int ends, bool allowed)
    {
        var now = DateTimeOffset.UtcNow;
        var scopes = new MutableScopes(new([new("grant", now.AddSeconds(starts), now.AddSeconds(ends),
            ["organization"], ["workspace"], [], ["SZ"], [], [], [], ["ReadCase"], false)], epoch, now.AddSeconds(expiry)));
        var authority = await new EllaiUiAuthorityResolver(scopes).ResolveAsync(Actor, "scope", default);
        Assert.Equal(allowed, authority.Features.Contains(EllaiNavigationManifest.FeatureId));
        Assert.Equal(allowed, authority.Features.Contains(EllaiDashboardUiManifest.FeatureId));
        Assert.Equal(allowed, authority.Features.Contains(EllaiCaseListUiManifest.FeatureId));
        Assert.Equal(allowed, authority.Features.Contains(EllaiCaseDetailUiManifest.FeatureId));
        Assert.Equal(allowed, authority.Capabilities.Contains(EllaiNavigationManifest.Capability));
        Assert.Contains(EllaiReportUiManifest.FeatureId, authority.Features);
        Assert.Equal("owner", authority.OwnerActorId);
        Assert.Equal("scope", authority.ScopeEpoch);
    }

    [Fact]
    public async Task MissingServicesAndMissingGrantsOnlyExposePublicReport()
    {
        var noService = await new EllaiUiAuthorityResolver().ResolveAsync(Actor, "scope", default);
        var empty = new MutableScopes(new([], 7, DateTimeOffset.UtcNow.AddMinutes(1)));
        var noGrant = await new EllaiUiAuthorityResolver(empty).ResolveAsync(Actor, "scope", default);
        var noActor = await new EllaiUiAuthorityResolver(empty).ResolveAsync(null, "scope", default);
        foreach (var authority in new[] { noService, noGrant, noActor })
        {
            Assert.Equal([EllaiReportUiManifest.FeatureId], authority.Features);
            Assert.Empty(authority.Capabilities);
        }
    }

    [Theory]
    [InlineData("navigation", 15)]
    [InlineData("dashboard", 8)]
    [InlineData("case-list", 18)]
    [InlineData("case-detail", 10)]
    public async Task BrokerRechecksRevocationInsteadOfTrustingSessionFeatureOrEarlierCatalogue(string feature, int count)
    {
        var now = DateTimeOffset.UtcNow;
        var scopes = new MutableScopes(new([new("grant", now.AddMinutes(-1), now.AddMinutes(1),
            ["organization"], ["workspace"], [], ["SZ"], [], [], [], ["ReadCase"], false)], 7, now.AddMinutes(1)));
        var registry = new EllaiUiRegistry();
        foreach (var control in EllaiNavigationManifest.Controls.Concat(EllaiDashboardUiManifest.Controls).Concat(EllaiCaseListUiManifest.Controls).Concat(EllaiCaseDetailUiManifest.Controls)) registry.Register(control);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), uiRegistry: registry,
            uiAuthority: new EllaiUiAuthorityResolver(scopes));
        var context = new EllaiToolContext("session", "scope", "public.product-help", null, 4, true,
            DataClassification: EllaiProcessingClassification.UserProvidedNonSensitive, Actor: Actor,
            ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);
        var request = new EllaiToolRequest(EllaiToolNames.UiCatalogue, new Dictionary<string, string> { ["feature"] = feature });
        var granted = await broker.InvokeAsync(context, request, default);
        Assert.True(granted.Allowed);
        Assert.Equal(count, granted.ItemCount);
        Assert.DoesNotContain("/my-cases", granted.ResultSummary);
        scopes.Unavailable = true;
        Assert.False((await broker.InvokeAsync(context, request, default)).Allowed);
        Assert.False((await broker.InvokeAsync(context with { FeatureId = feature }, request, default)).Allowed);
        Assert.Equal(3, scopes.Calls);
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoPublicHelpSuccess()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new EllaiUiAuthorityResolver().ResolveAsync(Actor, "scope", source.Token));
    }

    private sealed class MutableScopes(ScopeManifest manifest) : IScopeService
    {
        public bool Unavailable { get; set; }
        public int Calls { get; private set; }
        public ValueTask<ScopeManifest> GetAsync(ActorContext actor, CancellationToken cancellationToken)
        {
            Calls++;
            if (Unavailable) throw new ScopeUnavailableException(ScopeFailureCode.GrantMissing);
            return ValueTask.FromResult(manifest);
        }
    }
}
