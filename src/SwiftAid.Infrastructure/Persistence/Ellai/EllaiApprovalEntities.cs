namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>Authoritative row for one approved administrative action and its
/// receipt. The idempotency key is unique: a commit-then-timeout retry finds
/// the same row instead of a second effect. The plan/action triple is unique:
/// one action result per plan version.</summary>
public sealed class EllaiPlanApprovalEntity
{
    public string ApprovalId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string PlanHash { get; set; } = "";
    public int PlanVersion { get; set; }
    public string? ApprovedRecipient { get; set; }
    public DateTimeOffset? ApprovedDueAtUtc { get; set; }
    public string Status { get; set; } = "";
    public string PolicyHash { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? VerifiedAtUtc { get; set; }
}
