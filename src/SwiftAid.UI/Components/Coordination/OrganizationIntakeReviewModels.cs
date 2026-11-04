using SwiftAid.UI.Components.Theme;

namespace SwiftAid.UI.Components.Coordination;

/// <summary>State of the organization review queue after host authorization and result mapping.</summary>
public enum OrganizationIntakeReviewState
{
    Loading,
    Empty,
    Populated,
    Offline,
    Unavailable,
    Denied,
    Error
}

/// <summary>Review actions that are supported by the current intake decision contract.</summary>
public enum OrganizationIntakeReviewDecision
{
    RouteForReview,
    AcceptIntoCase,
    Decline
}

/// <summary>Capabilities computed by the host for the current authorized review record.</summary>
public sealed record OrganizationIntakeReviewCapabilities(
    bool CanRouteForReview,
    bool CanAcceptIntoCase,
    bool CanDecline);

/// <summary>Privacy-conscious queue item. ReceiptId is an internal callback key and is never rendered.</summary>
public sealed record OrganizationIntakeReviewQueueItem(
    Guid ReceiptId,
    string Status,
    string RoutingStatus,
    string SubjectScope,
    string CountryLabel,
    string? RegionLabel,
    DateTimeOffset ReceivedAtUtc,
    long Version);

/// <summary>Authorized organization review detail mapped from the typed intake review client.</summary>
public sealed record OrganizationIntakeReviewDetail(
    Guid ReceiptId,
    string Status,
    string RoutingStatus,
    string SubjectScope,
    string CountryLabel,
    string? RegionLabel,
    DateTimeOffset ReceivedAtUtc,
    long Version,
    string Narrative,
    IReadOnlyList<string> Observations,
    OrganizationIntakeReviewCapabilities Capabilities);

/// <summary>Expected-version decision request. AcceptIntoCase creates a new case; no target ID is accepted.</summary>
public sealed record OrganizationIntakeReviewCommand(
    Guid ReceiptId,
    long ExpectedVersion,
    OrganizationIntakeReviewDecision Decision,
    string? ReviewReason = null);

public static class OrganizationIntakeReviewStateExtensions
{
    public static SwiftAidViewState ToViewState(this OrganizationIntakeReviewState state) => state switch
    {
        OrganizationIntakeReviewState.Loading => SwiftAidViewState.Loading,
        OrganizationIntakeReviewState.Offline => SwiftAidViewState.Offline,
        OrganizationIntakeReviewState.Denied => SwiftAidViewState.Denied,
        OrganizationIntakeReviewState.Error => SwiftAidViewState.Error,
        OrganizationIntakeReviewState.Unavailable => SwiftAidViewState.Information,
        OrganizationIntakeReviewState.Empty => SwiftAidViewState.Empty,
        _ => SwiftAidViewState.Populated
    };
}

