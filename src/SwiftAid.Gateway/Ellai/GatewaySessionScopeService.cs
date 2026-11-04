using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Sync;
using SwiftAid.Infrastructure.Authorization;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Each concurrent circuit or agent read owns its database context.</summary>
public sealed class GatewaySessionScopeService(IServiceScopeFactory scopes) : IScopeService
{
    public async ValueTask<ScopeManifest> GetAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PostgresSessionScopeService>()
            .GetAsync(actor, cancellationToken);
    }
}
