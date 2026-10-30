namespace SwiftAid.Contracts.Ellai;

/// <summary>Session UI event as delivered by the review event transport.</summary>
public sealed record EllaiSessionEventDto(
    long Sequence,
    string SessionId,
    string Kind,
    string? Ref,
    string Detail,
    DateTimeOffset AtUtc);

/// <summary>Bounded poll page with the cursor the client passes back.</summary>
public sealed record EllaiSessionEventPageDto(
    IReadOnlyList<EllaiSessionEventDto> Events,
    long Cursor);

/// <summary>Open review for dialog rendering: identity, binding, state and
/// safe display rows. Approval still resolves server-side.</summary>
public sealed record EllaiReviewDto(
    string RequestId,
    string SessionId,
    string PlanHash,
    int PlanVersion,
    string State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<EllaiApprovalActionDescriptor> Actions);

/// <summary>Approved receipt identity for the dialog confirmation path.</summary>
public sealed record EllaiApprovedReceiptDto(
    string ReceiptId,
    string ActionId,
    string Status);

/// <summary>Review client port shared by the in-process gateway adapter and
/// the HTTP transport. Null means unavailable or inapplicable: the caller
/// keeps its cursor and retries; replay never replays commands.</summary>
public interface IEllaiReviewClient
{
    /// <summary>Starts a durable session for the requested mode and returns
    /// its server-issued view. Null means unavailable: the caller must not
    /// invent a session id.</summary>
    Task<EllaiSessionView?> StartSessionAsync(EllaiMode mode, CancellationToken cancellationToken);

    Task<EllaiSessionEventPageDto?> PollEventsAsync(
        string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken);

    Task<EllaiReviewDto?> GetActiveReviewAsync(string sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(
        string sessionId, string requestId, IReadOnlyList<string> actionIds,
        CancellationToken cancellationToken);

    Task<bool> RejectReviewAsync(
        string sessionId, string requestId, CancellationToken cancellationToken);
}
