using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Sync;

namespace SwiftAid.Application.Ellai;

/// <summary>Resolves the scope attached to a new Ellai session. When the
/// organization scope source is unavailable, a signed-in account may still
/// start a product-help session that carries no workspace or case grants.</summary>
public sealed class EllaiSessionScopeResolver
{
    private static readonly TimeSpan IdentityHelpScopeLifetime = TimeSpan.FromMinutes(10);
    private readonly IScopeService _scopeService;
    private readonly TimeProvider _timeProvider;

    public EllaiSessionScopeResolver(IScopeService scopeService, TimeProvider? timeProvider = null)
    {
        _scopeService = scopeService ?? throw new ArgumentNullException(nameof(scopeService));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ScopeManifest> GetAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        try
        {
            return await _scopeService.GetAsync(actor, cancellationToken).ConfigureAwait(false);
        }
        catch (ScopeUnavailableException)
        {
            // Session creation exposes no workspace data or tools. The empty
            // scope lets the account reach public product help while all
            // privileged reads and actions continue to require a fresh scope.
            return new ScopeManifest(
                Array.Empty<ScopeGrantManifest>(),
                actor.AccessEpoch,
                _timeProvider.GetUtcNow().Add(IdentityHelpScopeLifetime));
        }
    }
}
