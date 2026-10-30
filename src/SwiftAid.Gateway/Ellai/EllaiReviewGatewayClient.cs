using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Server-side review client for Blazor Server: resolves the current
/// principal per call and delegates to the in-process journal, coordinator
/// and approval service. Mirrors the HTTP endpoint semantics exactly;
/// unavailable dependencies read as unavailable, never as fabricated data.
/// Guests have no approval reviews: unauthenticated calls return empty.</summary>
public sealed class EllaiReviewGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider) : IEllaiReviewClient
{
    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public async Task<EllaiSessionView?> StartSessionAsync(
        EllaiMode mode, CancellationToken cancellationToken)
    {
        if (!await IsAuthenticatedAsync().ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var sessions = _serviceProvider.GetService<EllaiSessionService>();
            if (sessions is null)
            {
                return null;
            }

            return await sessions.StartAsync(
                new StartSessionRequest(
                    mode, Guid.NewGuid().ToString("N"), null, null, null, null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<EllaiSessionEventPageDto?> PollEventsAsync(
        string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken)
    {
        if (!await IsAuthenticatedAsync().ConfigureAwait(false))
        {
            return null;
        }

        var journals = _serviceProvider.GetService<EllaiSessionEventJournal>();
        if (journals is null)
        {
            return null;
        }

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 0, 30));
            while (true)
            {
                var page = journals.ReadSince(sessionId, Math.Max(0, cursor));
                if (page.Events.Count > 0 || DateTimeOffset.UtcNow >= deadline)
                {
                    return new EllaiSessionEventPageDto(
                        page.Events.Select(e => new EllaiSessionEventDto(
                            e.Sequence, e.SessionId, e.Kind, e.Ref, e.Detail, e.AtUtc)).ToList(),
                        page.Cursor);
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public Task<EllaiReviewDto?> GetActiveReviewAsync(string sessionId, CancellationToken cancellationToken) =>
        WithAuthenticatedAsync<EllaiReviewDto?>(dialogs =>
        {
            var active = dialogs.TryGetActive(sessionId, DateTimeOffset.UtcNow);
            return Task.FromResult(active is null ? null : MapReview(active));
        });

    public async Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(
        string sessionId, string requestId, IReadOnlyList<string> actionIds,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthenticatedAsync().ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var dialogs = _serviceProvider.GetService<EllaiApprovalDialogCoordinator>();
            var approvals = _serviceProvider.GetService<EllaiApprovalService>();
            var journals = _serviceProvider.GetService<EllaiSessionEventJournal>();
            if (dialogs is null || approvals is null || journals is null
                || actionIds is null || actionIds.Count == 0
                || actionIds.Count > EllaiDecisionValidator.MaxPlanActions)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var review = dialogs.Get(requestId);
            if (review is null
                || !string.Equals(review.SessionId, sessionId, StringComparison.Ordinal)
                || review.State != EllaiApprovalDialogState.AwaitingApproval)
            {
                return null;
            }

            var receipts = await approvals.ApproveReviewedAsync(
                review, actionIds, now, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (receipts is null)
            {
                return null;
            }

            dialogs.Resolve(requestId, approved: true, now);
            journals.Append(sessionId, "approval-resolved", requestId, "Approved", now);
            return receipts.Select(receipt => new EllaiApprovedReceiptDto(
                receipt.ReceiptId, receipt.ActionId, receipt.Status.ToString())).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> RejectReviewAsync(
        string sessionId, string requestId, CancellationToken cancellationToken)
    {
        if (!await IsAuthenticatedAsync().ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            var dialogs = _serviceProvider.GetService<EllaiApprovalDialogCoordinator>();
            var journals = _serviceProvider.GetService<EllaiSessionEventJournal>();
            if (dialogs is null || journals is null)
            {
                return false;
            }

            var review = dialogs.Get(requestId);
            if (review is null
                || !string.Equals(review.SessionId, sessionId, StringComparison.Ordinal)
                || review.State != EllaiApprovalDialogState.AwaitingApproval)
            {
                return false;
            }

            dialogs.Resolve(requestId, approved: false, DateTimeOffset.UtcNow);
            journals.Append(sessionId, "approval-resolved", requestId, "Rejected", DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static EllaiReviewDto MapReview(EllaiApprovalDialogRequest review) => new(
        review.RequestId, review.SessionId, review.PlanHash, review.PlanVersion,
        review.State.ToString(), review.CreatedAtUtc, review.ExpiresAtUtc, review.Actions);

    private async Task<bool> IsAuthenticatedAsync()
    {
        try
        {
            var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            return state.User?.Identity?.IsAuthenticated == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<T?> WithAuthenticatedAsync<T>(
        Func<EllaiApprovalDialogCoordinator, Task<T?>> action)
    {
        if (!await IsAuthenticatedAsync().ConfigureAwait(false))
        {
            return default;
        }

        try
        {
            var dialogs = _serviceProvider.GetService<EllaiApprovalDialogCoordinator>();
            if (dialogs is null)
            {
                return default;
            }

            return await action(dialogs).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return default;
        }
    }
}
