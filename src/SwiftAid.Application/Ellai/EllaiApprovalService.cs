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
/// effects audit against the exact guardrails in force.</summary>
public sealed record EllaiReceipt(
    string ReceiptId,
    string SessionId,
    string ActionId,
    string IdempotencyKey,
    EllaiReceiptStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ApprovalExpiresAtUtc,
    DateTimeOffset? VerifiedAtUtc,
    string PolicyHash = "");

/// <summary>Thread-safe in-memory receipt store. Production persistence must
/// implement the same semantics with transactional writes.</summary>
public sealed class EllaiReceiptStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EllaiReceipt> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EllaiReceipt> _byId = new(StringComparer.Ordinal);

    public EllaiReceipt Record(
        string sessionId, string actionId, string idempotencyKey, DateTimeOffset now, TimeSpan approvalLifetime,
        string policyHash = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        lock (_gate)
        {
            if (_byKey.TryGetValue(idempotencyKey, out var existing))
            {
                return existing;
            }

            var receipt = new EllaiReceipt(
                $"rcpt-{Guid.NewGuid():N}", sessionId, actionId, idempotencyKey,
                EllaiReceiptStatus.Accepted, now, now.Add(approvalLifetime), null, policyHash);
            _byKey[idempotencyKey] = receipt;
            _byId[receipt.ReceiptId] = receipt;
            return receipt;
        }
    }

    public EllaiReceipt? Get(string receiptId)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(receiptId, out var receipt) ? receipt : null;
        }
    }

    public IReadOnlyList<EllaiReceipt> ListBySession(string sessionId)
    {
        lock (_gate)
        {
            return _byId.Values.Where(r => r.SessionId == sessionId).ToList();
        }
    }

    /// <summary>Marks the receipt status. Expired approvals cannot become
    /// Applied: verification after expiry records UnknownOutcome.</summary>
    public EllaiReceipt? MarkStatus(string receiptId, EllaiReceiptStatus status, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(receiptId, out var current))
            {
                return null;
            }

            var effective = status == EllaiReceiptStatus.Applied && now > current.ApprovalExpiresAtUtc
                ? EllaiReceiptStatus.UnknownOutcome
                : status;
            var next = current with { Status = effective, VerifiedAtUtc = now };
            _byId[receiptId] = next;
            _byKey[next.IdempotencyKey] = next;
            return next;
        }
    }
}

/// <summary>Approval gate + receipt submission (ELL-T08 completion).
/// Exact-approval matching stays in <see cref="EllaiApprovedActionService"/>;
/// this service adds expiry (15 minutes) and idempotent receipts so a
/// commit-then-timeout retry returns the one stable receipt.</summary>
public sealed class EllaiApprovalService(
    EllaiApprovedActionService actions,
    EllaiReceiptStore receipts)
{
    public static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(15);

    public EllaiReceipt? SubmitApproval(
        string sessionId,
        string planHash, int planVersion, AdminActionProposal proposal,
        string approvedPlanHash, int approvedPlanVersion,
        string? approvedRecipient, DateTimeOffset? approvedDueAtUtc,
        DateTimeOffset now,
        string policyHash = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var action = actions.Submit(
            planHash, planVersion, proposal, approvedPlanHash, approvedPlanVersion,
            approvedRecipient, approvedDueAtUtc);
        if (action is null)
        {
            return null;
        }

        return receipts.Record(sessionId, action.ActionId, action.IdempotencyKey, now, ApprovalLifetime, policyHash);
    }

    public EllaiReceipt? VerifyReceipt(string receiptId, EllaiReceiptStatus status, DateTimeOffset now) =>
        receipts.MarkStatus(receiptId, status, now);
}
