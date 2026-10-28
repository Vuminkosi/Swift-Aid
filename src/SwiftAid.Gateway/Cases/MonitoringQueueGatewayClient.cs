using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

/// <summary>
/// Server-side monitoring queue client for Blazor Server: resolves the current staff
/// principal per call and delegates to the authorized query. All data rules
/// (per-case ReadCase enforcement, skipped denials) are enforced by the query,
/// not by this adapter. Deployments without the case command pipeline fail closed
/// with the same unavailable outcome.
/// </summary>
public sealed class MonitoringQueueGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider) : IMonitoringQueueClient
{
    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public async Task<MonitoringQueueResult> GetAssignedAsync(
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Denied, []);
        }

        var queue = _serviceProvider.GetService<ICaseMonitoringQueueQuery>();
        if (queue is null)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Unavailable, []);
        }

        try
        {
            return await queue.GetAssignedAsync(actor, limit, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Unavailable, []);
        }
    }

    public async Task<MonitoringQueueResult> GetOpenAsync(
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Denied, []);
        }

        var queue = _serviceProvider.GetService<ICaseMonitoringQueueQuery>();
        if (queue is null)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Unavailable, []);
        }

        try
        {
            return await queue.GetOpenAsync(actor, limit, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Unavailable, []);
        }
    }

    private async Task<ActorContext?> GetActorAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = state.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        try
        {
            return ActorContext.FromValidatedPrincipal(principal);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
