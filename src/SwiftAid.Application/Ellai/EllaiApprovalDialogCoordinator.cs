using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Server-side dialog states for one approval-gated plan.
/// Distinct from the business receipt lattice: these track the human review,
/// not the effect.</summary>
public enum EllaiApprovalDialogState
{
    AwaitingApproval,
    Queued,
    Approved,
    Rejected,
    Cancelled,
    Superseded,
    Expired
}

/// <summary>One server-tracked approval review, carrying the safe display
/// rows the dialog renders. Rows are display data; approval still binds
/// the exact plan hash and version.</summary>
public sealed record EllaiApprovalDialogRequest(
    string RequestId,
    string SessionId,
    string PlanHash,
    int PlanVersion,
    EllaiApprovalDialogState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<EllaiApprovalActionDescriptor> Actions);

/// <summary>Server-side half of the mandatory approval dialog contract.
/// Enforces one active review per session (duplicate request ids focus the
/// same review; different requests queue explicitly), edit-then-new-version
/// invalidation, and terminal session-cancel fencing. In-memory like the
/// current receipt store; the durable cross-instance port is Task 3 work.
/// The Blazor dialog host that issues these request ids is still to come;
/// until then the gateway approval endpoint consults this coordinator only
/// when the caller presents a request id.</summary>
public sealed class EllaiApprovalDialogCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EllaiApprovalDialogRequest> _byId = new(StringComparer.Ordinal);

    /// <summary>Registers a validated pending plan for human review.
    /// Returns the active request and whether it was newly created: a repeated
    /// event id returns the same active review instead of stacking dialogs.</summary>
    public (EllaiApprovalDialogRequest Request, bool Created) Register(
        string sessionId, string requestId, string planHash, int planVersion,
        DateTimeOffset now, TimeSpan lifetime,
        IReadOnlyList<EllaiApprovalActionDescriptor>? actions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);

        lock (_gate)
        {
            SweepSessionLocked(sessionId, now);
            if (_byId.TryGetValue(requestId, out var existing)
                && string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal)
                && IsOpen(existing.State))
            {
                return (existing, false);
            }

            var active = ActiveLocked(sessionId);
            var request = new EllaiApprovalDialogRequest(
                requestId, sessionId, planHash, planVersion,
                active is null ? EllaiApprovalDialogState.AwaitingApproval : EllaiApprovalDialogState.Queued,
                now, now.Add(lifetime),
                actions ?? Array.Empty<EllaiApprovalActionDescriptor>());
            _byId[requestId] = request;
            return (request, true);
        }
    }

    /// <summary>Applies a human edit: the reviewed binding is superseded and
    /// a new exact plan version enters review. The old hash/version can
    /// never be approved afterwards.</summary>
    public EllaiApprovalDialogRequest? ApplyEdit(
        string requestId, string newRequestId, string newPlanHash, int newPlanVersion,
        DateTimeOffset now, TimeSpan lifetime,
        IReadOnlyList<EllaiApprovalActionDescriptor>? actions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPlanHash);

        lock (_gate)
        {
            if (!_byId.TryGetValue(requestId, out var current) || !IsOpen(current.State))
            {
                return null;
            }

            _byId[requestId] = current with { State = EllaiApprovalDialogState.Superseded };
            var next = new EllaiApprovalDialogRequest(
                newRequestId, current.SessionId, newPlanHash, newPlanVersion,
                EllaiApprovalDialogState.AwaitingApproval, now, now.Add(lifetime),
                actions ?? Array.Empty<EllaiApprovalActionDescriptor>());
            _byId[newRequestId] = next;
            return next;
        }
    }

    /// <summary>Records the human choice and promotes the oldest queued
    /// review, if any. Returns null for unknown or already-closed ids:
    /// reconciliation looks reviews up by original id, never by replay.</summary>
    public EllaiApprovalDialogRequest? Resolve(string requestId, bool approved, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(requestId, out var current) || !IsOpen(current.State))
            {
                return null;
            }

            var resolved = current with
            {
                State = approved ? EllaiApprovalDialogState.Approved : EllaiApprovalDialogState.Rejected
            };
            _byId[requestId] = resolved;
            PromoteLocked(current.SessionId, now);
            return resolved;
        }
    }

    /// <summary>Terminal session cancellation: every open review in the
    /// session closes at once and no queued review is promoted.</summary>
    public int CancelSession(string sessionId, DateTimeOffset now)
    {
        lock (_gate)
        {
            SweepSessionLocked(sessionId, now);
            var open = _byId.Values
                .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal) && IsOpen(r.State))
                .ToList();
            foreach (var request in open)
            {
                _byId[request.RequestId] = request with { State = EllaiApprovalDialogState.Cancelled };
            }

            return open.Count;
        }
    }

    /// <summary>Current active review for the session fence, if any.
    /// Expired reviews report Expired and release the active slot.</summary>
    public EllaiApprovalDialogRequest? TryGetActive(string sessionId, DateTimeOffset now)
    {
        lock (_gate)
        {
            SweepSessionLocked(sessionId, now);
            return ActiveLocked(sessionId);
        }
    }

    public EllaiApprovalDialogRequest? Get(string requestId)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(requestId, out var request) ? request : null;
        }
    }

    private static bool IsOpen(EllaiApprovalDialogState state) =>
        state is EllaiApprovalDialogState.AwaitingApproval or EllaiApprovalDialogState.Queued;

    private EllaiApprovalDialogRequest? ActiveLocked(string sessionId) => _byId.Values.FirstOrDefault(
        r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal)
            && r.State == EllaiApprovalDialogState.AwaitingApproval);

    private void PromoteLocked(string sessionId, DateTimeOffset now)
    {
        if (ActiveLocked(sessionId) is not null)
        {
            return;
        }

        var next = _byId.Values
            .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal)
                && r.State == EllaiApprovalDialogState.Queued)
            .OrderBy(r => r.CreatedAtUtc)
            .FirstOrDefault();
        if (next is not null && next.ExpiresAtUtc > now)
        {
            _byId[next.RequestId] = next with { State = EllaiApprovalDialogState.AwaitingApproval };
        }
        else if (next is not null)
        {
            _byId[next.RequestId] = next with { State = EllaiApprovalDialogState.Expired };
            PromoteLocked(sessionId, now);
        }
    }

    private void SweepSessionLocked(string sessionId, DateTimeOffset now)
    {
        foreach (var request in _byId.Values
            .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal)
                && r.State == EllaiApprovalDialogState.AwaitingApproval
                && r.ExpiresAtUtc <= now)
            .ToList())
        {
            _byId[request.RequestId] = request with { State = EllaiApprovalDialogState.Expired };
        }

        PromoteLocked(sessionId, now);
    }
}
