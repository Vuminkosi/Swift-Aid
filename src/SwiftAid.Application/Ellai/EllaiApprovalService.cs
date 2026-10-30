using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>E03 outcome lattice for an approved administrative action.
/// AcceptedUnverified/UnknownOutcome/PartiallyApplied are first-class:
/// commit-then-timeout never reports success it cannot prove.</summary>
public enum EllaiReceiptStatus
{
    Accepted,
    AcceptedUnverified,
    UnknownOutcome,
    PartiallyApplied,
    Applied,
    Failed
}

/// <summary>Durable receipt for one approved action, keyed idempotently.
/// <see cref="PolicyHash"/> stamps the pinned runtime policy so spend and
/// effects audit against the exact guardrails in force. The approved
/// recipient and due time bind the exact human-confirmed fields.</summary>
public sealed record EllaiReceipt(
    string ReceiptId,
    string SessionId,
    string ActionId,
    string IdempotencyKey,
    EllaiReceiptStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ApprovalExpiresAtUtc,
    DateTimeOffset? VerifiedAtUtc,
    string PolicyHash = "",
    string? ApprovedRecipient = null,
    DateTimeOffset? ApprovedDueAtUtc = null);

/// <summary>Durable approval/receipt authority. PostgreSQL is the production
/// implementation; the in-memory store keeps unit-test semantics identical.
/// Record is idempotent by idempotency key: a commit-then-timeout retry
/// returns the one stable receipt instead of a second effect.</summary>
public interface IEllaiApprovalStore
{
    Task<EllaiReceipt> RecordAsync(
        string sessionId, string actionId, string idempotencyKey,
        string planHash, int planVersion,
        string? approvedRecipient, DateTimeOffset? approvedDueAtUtc,
        DateTimeOffset now, TimeSpan approvalLifetime, string policyHash,
        CancellationToken cancellationToken);

    Task<EllaiReceipt?> GetAsync(string receiptId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EllaiReceipt>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Marks the receipt status. Expired approvals cannot become
    /// Applied: verification after expiry records UnknownOutcome.</summary>
    Task<EllaiReceipt?> MarkStatusAsync(
        string receiptId, EllaiReceiptStatus status, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Thread-safe in-memory approval store with the same semantics as
/// the durable port. Enforces the one-action-result-per-plan-version rule
/// alongside idempotency-key stability.</summary>
public sealed class EllaiReceiptStore : IEllaiApprovalStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EllaiReceipt> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EllaiReceipt> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byPlanAction = new(StringComparer.Ordinal);

    public Task<EllaiReceipt> RecordAsync(
        string sessionId, string actionId, string idempotencyKey,
        string planHash, int planVersion,
        string? approvedRecipient, DateTimeOffset? approvedDueAtUtc,
        DateTimeOffset now, TimeSpan approvalLifetime, string policyHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);

        lock (_gate)
        {
            if (_byKey.TryGetValue(idempotencyKey, out var existing))
            {
                return Task.FromResult(existing);
            }

            var planAction = $"{planHash}|{planVersion}|{actionId}";
            if (_byPlanAction.TryGetValue(planAction, out var conflictingId)
                && _byId.TryGetValue(conflictingId, out var conflicting)
                && !string.Equals(conflicting.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A different approved action already binds this plan version and action.");
            }

            var receipt = new EllaiReceipt(
                $"rcpt-{Guid.NewGuid():N}", sessionId, actionId, idempotencyKey,
                EllaiReceiptStatus.Accepted, now, now.Add(approvalLifetime), null, policyHash,
                approvedRecipient, approvedDueAtUtc);
            _byKey[idempotencyKey] = receipt;
            _byId[receipt.ReceiptId] = receipt;
            _byPlanAction[planAction] = receipt.ReceiptId;
            return Task.FromResult(receipt);
        }
    }

    public Task<EllaiReceipt?> GetAsync(string receiptId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_byId.TryGetValue(receiptId, out var receipt) ? receipt : null);
        }
    }

    public Task<IReadOnlyList<EllaiReceipt>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EllaiReceipt>>(
                _byId.Values.Where(r => r.SessionId == sessionId).ToList());
        }
    }

    public Task<EllaiReceipt?> MarkStatusAsync(
        string receiptId, EllaiReceiptStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        lock (_gate)
        {
            if (!_byId.TryGetValue(receiptId, out var current))
            {
                return Task.FromResult<EllaiReceipt?>(null);
            }

            var effective = status == EllaiReceiptStatus.Applied && now > current.ApprovalExpiresAtUtc
                ? EllaiReceiptStatus.UnknownOutcome
                : status;
            var next = current with { Status = effective, VerifiedAtUtc = now };
            _byId[receiptId] = next;
            _byKey[next.IdempotencyKey] = next;
            return Task.FromResult<EllaiReceipt?>(next);
        }
    }
}

/// <summary>Approval gate + receipt submission (ELL-T08 completion).
/// Exact-approval matching stays in <see cref="EllaiApprovedActionService"/>;
/// this service adds expiry (15 minutes) and idempotent durable receipts so a
/// commit-then-timeout retry returns the one stable receipt.</summary>
public sealed class EllaiApprovalService(
    EllaiApprovedActionService actions,
    IEllaiApprovalStore receipts)
{
    public static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(15);

    public async Task<EllaiReceipt?> SubmitApprovalAsync(
        string sessionId,
        string planHash, int planVersion, AdminActionProposal proposal,
        string approvedPlanHash, int approvedPlanVersion,
        string? approvedRecipient, DateTimeOffset? approvedDueAtUtc,
        DateTimeOffset now,
        string policyHash = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var action = actions.Submit(
            planHash, planVersion, proposal, approvedPlanHash, approvedPlanVersion,
            approvedRecipient, approvedDueAtUtc);
        if (action is null)
        {
            return null;
        }

        return await receipts.RecordAsync(sessionId, action.ActionId, action.IdempotencyKey,
            planHash, planVersion, approvedRecipient, approvedDueAtUtc,
            now, ApprovalLifetime, policyHash, cancellationToken).ConfigureAwait(false);
    }

    public Task<EllaiReceipt?> VerifyReceiptAsync(
        string receiptId, EllaiReceiptStatus status, DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        receipts.MarkStatusAsync(receiptId, status, now, cancellationToken);
}
