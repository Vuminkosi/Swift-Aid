using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

/// <summary>
/// Server-side timeline client for Blazor Server: resolves the current staff
/// principal per call and delegates to the authorized query. All data rules
/// (ReadCase enforcement, denial/absence sharing) are enforced by the query,
/// not by this adapter. Deployments without the case command pipeline fail
/// closed with the same unavailable outcome.
/// </summary>
public sealed class CaseTimelineGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider) : ICaseTimelineClient
{
    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public async Task<CaseTimelineResult> GetAsync(
        Guid caseId,
        int? limit,
        CancellationToken cancellationToken = default,
        long? beforeAggregateVersion = null)
    {
        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null || caseId == Guid.Empty)
        {
            return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        }

        await using var scope = _serviceProvider.CreateAsyncScope();
        var timeline = scope.ServiceProvider.GetService<ICaseTimelineQuery>();
        if (timeline is null)
        {
            return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        }

        try
        {
            return await timeline.GetAsync(actor, caseId, limit, cancellationToken, beforeAggregateVersion).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
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
