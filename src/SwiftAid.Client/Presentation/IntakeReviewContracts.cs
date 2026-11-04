namespace SwiftAid.Client.Presentation;

public enum IntakeReviewDecision
{
    RouteForReview,
    AcceptIntoCase,
    Decline
}

public enum IntakeReviewState
{
    RoutedForReview,
    AcceptedIntoCase,
    Declined
}

public sealed record IntakeReviewQuery(
    PageRequest Page,
    IntakeReviewState? State = null)
{
    public bool IsValid => Page is { IsValid: true, PageNumber: <= 100_000 } &&
        (State is null || Enum.IsDefined(State.Value));
}

public sealed record IntakeReviewCapabilities(
    bool CanRouteForReview,
    bool CanAcceptIntoCase,
    bool CanDecline);

public sealed record IntakeReviewQueueItem(
    Guid ReceiptId,
    string Status,
    string RoutingStatus,
    string CountryCode,
    string? RegionLabel,
    string SubjectScope,
    DateTimeOffset ReceivedAtUtc,
    long ExpectedVersion,
    IntakeReviewCapabilities Capabilities);

public sealed record IntakeReviewDetail(
    IntakeReviewQueueItem Item,
    string Narrative,
    IReadOnlyList<string> Observations);

public sealed record IntakeReviewCommand(
    Guid ReceiptId,
    long ExpectedVersion,
    IntakeReviewDecision Decision,
    string? ReviewReason = null)
{
    public bool IsValid => ReceiptId != Guid.Empty && ExpectedVersion > 0 &&
        Enum.IsDefined(Decision) && (ReviewReason is null || ReviewReason.Trim().Length <= 1000);
}

public sealed record IntakeReviewOutcome(
    Guid ReceiptId,
    string Status,
    string RoutingStatus,
    long? ResultingVersion,
    Guid? PromotedCaseId = null);

/// <summary>Reviewer read and decision capability. Unsupported live reads remain unavailable.</summary>
public interface IIntakeReviewClient
{
    Task<UiCapabilityResult<Page<IntakeReviewQueueItem>>> ListAsync(
        IntakeReviewQuery query,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<IntakeReviewDetail>> GetAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<IntakeReviewOutcome>> ReviewAsync(
        IntakeReviewCommand command,
        CancellationToken cancellationToken = default);
}
