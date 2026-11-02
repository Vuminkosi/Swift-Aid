using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Fresh server authority for UI metadata. Navigation grants no case read or action permission.</summary>
public sealed class EllaiUiAuthorityResolver(IScopeService? scopes = null, TimeProvider? clock = null)
{
    public async ValueTask<EllaiUiEntitlements> ResolveAsync(ActorContext? actor, string scopeEpoch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        HashSet<string> features = [EllaiReportUiManifest.FeatureId];
        HashSet<string> capabilities = [];
        if (actor is not null && scopes is not null)
        {
            try
            {
                var scope = await scopes.GetAsync(actor, ct).ConfigureAwait(false);
                var now = (clock ?? TimeProvider.System).GetUtcNow();
                if (scope.AccessEpoch == actor.AccessEpoch && scope.ExpiresAtUtc > now &&
                    scope.Grants.Any(grant => grant.ValidFromUtc <= now && grant.ExpiresAtUtc > now))
                {
                    features.Add(EllaiNavigationManifest.FeatureId);
                    features.Add(EllaiDashboardUiManifest.FeatureId);
                    features.Add(EllaiCaseListUiManifest.FeatureId);
                    features.Add(EllaiCaseDetailUiManifest.FeatureId);
                    capabilities.Add(EllaiNavigationManifest.Capability);
                }
            }
            catch (ScopeUnavailableException)
            {
                // Public help remains available; missing/revoked scope never grants private navigation.
            }
        }
        return new(actor?.ActorId ?? string.Empty, scopeEpoch, features, capabilities);
    }
}
