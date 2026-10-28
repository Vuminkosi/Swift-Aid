using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Gateway.Intake;

/// <summary>
/// Server-side coordination client for Blazor Server: resolves the current staff
/// principal per call and delegates to the authorized application service. All data
/// rules (minimum-necessary projections, coordinator-only visibility) are enforced
/// by the service, not by this adapter.
/// </summary>
public sealed class IntakeCoordinationGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IIntakeCoordinationService coordination) : IIntakeCoordinationClient
{
    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IIntakeCoordinationService _coordination =
        coordination ?? throw new ArgumentNullException(nameof(coordination));

    public async Task<(CoordinationQueueAccess Access, IReadOnlyList<IntakeCoordinationQueueEntry> Entries)> GetQueueAsync(
        IntakeRoutingStatus? routingStatus,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return (CoordinationQueueAccess.Denied, []);
        }

        try
        {
            var entries = await _coordination.GetQueueAsync(actor, routingStatus, limit, null, null, cancellationToken)
                .ConfigureAwait(false);
            return (CoordinationQueueAccess.Allowed, entries);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (CoordinationQueueAccess.Unavailable, []);
        }
        catch (Exception)
        {
            return (CoordinationQueueAccess.Unavailable, []);
        }
    }

    public async Task<IntakeCoordinationDetail?> GetDetailAsync(Guid receiptId, CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null || receiptId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return await _coordination.GetDetailAsync(actor, receiptId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<IntakeCoordinationQueueOption>> GetQueuesAsync(
        CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return [];
        }

        try
        {
            return await _coordination.GetEligibleQueuesAsync(actor, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];
        }
    }

    public async Task<CoordinationActionOutcome> ActAsync(
        Guid receiptId,
        long expectedVersion,
        IntakeCoordinationAction action,
        string? targetQueueId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return CoordinationActionOutcome.NotFound;
        }

        try
        {
            var result = await _coordination.ActAsync(
                new IntakeCoordinationCommand(receiptId, expectedVersion, action, targetQueueId, reason),
                actor,
                cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                IntakeDecisionStatus.Applied => CoordinationActionOutcome.Applied,
                IntakeDecisionStatus.Conflict => CoordinationActionOutcome.Conflict,
                IntakeDecisionStatus.Invalid => CoordinationActionOutcome.Invalid,
                IntakeDecisionStatus.NotFoundOrNotAuthorized => CoordinationActionOutcome.NotFound,
                _ => CoordinationActionOutcome.Unavailable
            };
        }
        catch (Exception)
        {
            return CoordinationActionOutcome.Unavailable;
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
