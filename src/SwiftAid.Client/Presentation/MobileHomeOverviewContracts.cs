namespace SwiftAid.Client.Presentation;

public enum MobileHomeWorkCountKind
{
    /// <summary>Cases currently assigned to the signed-in worker and not administratively closed.</summary>
    AssignedCases,

    /// <summary>Open follow-ups with a due time at or before the current snapshot time.</summary>
    DueFollowUps,

    /// <summary>Open follow-ups past their due time; this is a subset of DueFollowUps.</summary>
    OverdueFollowUps,

    /// <summary>Unresolved assistance requests visible in the officer's authorized open-work scope.</summary>
    UnresolvedAssistanceRequests,

    /// <summary>Escalations assigned to the worker that have not been resolved.</summary>
    UnresolvedEscalations
}

public enum MobileHomeValueState
{
    Available,
    Suppressed,
    Unavailable
}

public enum MobileHomeDataReason
{
    SmallGroupSuppression,
    NotCollected,
    SourceUnavailable
}

public enum MobileHomeCompleteness
{
    Complete,
    Partial
}

public enum MobileHomeFreshnessState
{
    Current,
    Stale,
    Unknown
}

public enum MobileHomeActivityKind
{
    CaseReported,
    FollowUpCompleted,
    AssignmentReceived,
    AssistanceProvided,
    EscalationOpened
}

public enum MobileHomeActivityDestination
{
    Cases,
    FollowUps,
    Assistance
}

/// <summary>A bounded home request; the signed-in service determines identity and authorized scope.</summary>
public sealed record MobileHomeOverviewQuery(int RecentActivityLimit = 5)
{
    public bool IsValid => RecentActivityLimit is >= 0 and <= 5;
}

/// <summary>A count of assigned work, with the kind defining its business meaning.</summary>
public sealed record MobileHomeWorkCount(
    MobileHomeWorkCountKind Kind,
    MobileHomeValueState State,
    long? Value,
    MobileHomeDataReason? Reason = null)
{
    public bool IsValid => Enum.IsDefined(Kind) && Enum.IsDefined(State) &&
        (State == MobileHomeValueState.Available
            ? Value is >= 0 && Reason is null
            : Value is null && Reason.HasValue && Enum.IsDefined(Reason.Value) &&
                (State != MobileHomeValueState.Suppressed || Reason == MobileHomeDataReason.SmallGroupSuppression));
}

/// <summary>One controlled, nonclinical event with an authorized destination.</summary>
public sealed record MobileHomeActivity(
    string ActivityId,
    MobileHomeActivityKind Kind,
    DateTimeOffset OccurredAtUtc,
    MobileHomeActivityDestination Destination,
    string? DestinationId = null)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(ActivityId) && ActivityId.Length <= 128 &&
        ActivityId.All(IsOpaqueIdentifierCharacter) &&
        Enum.IsDefined(Kind) && OccurredAtUtc != default && Enum.IsDefined(Destination) &&
        (DestinationId is null || (DestinationId.Length is > 0 and <= 128 &&
            DestinationId.All(IsOpaqueIdentifierCharacter))) && IsDestinationValid(Kind, Destination);

    private static bool IsOpaqueIdentifierCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_';

    private static bool IsDestinationValid(
        MobileHomeActivityKind kind,
        MobileHomeActivityDestination destination) => kind switch
    {
        MobileHomeActivityKind.CaseReported or
        MobileHomeActivityKind.AssignmentReceived or
        MobileHomeActivityKind.EscalationOpened => destination == MobileHomeActivityDestination.Cases,
        MobileHomeActivityKind.FollowUpCompleted => destination == MobileHomeActivityDestination.FollowUps,
        MobileHomeActivityKind.AssistanceProvided => destination == MobileHomeActivityDestination.Assistance,
        _ => false
    };
}

/// <summary>A privacy-minimized mobile home projection returned after service-side authorization.</summary>
public sealed record MobileHomeOverview(
    string? VerifiedDisplayName,
    string? AuthorizedWorkspaceLabel,
    string? AuthorizedRegionLabel,
    DateTimeOffset AsOfUtc,
    DateTimeOffset? FreshnessExpiresAtUtc,
    MobileHomeFreshnessState FreshnessState,
    MobileHomeCompleteness Completeness,
    IReadOnlyList<MobileHomeWorkCount> WorkCounts,
    IReadOnlyList<MobileHomeActivity> RecentActivity)
{
    /// <summary>Expires current data using the consumer's clock, including cached responses.</summary>
    public MobileHomeFreshnessState EvaluateFreshness(DateTimeOffset evaluatedAtUtc)
    {
        if (evaluatedAtUtc == default)
            throw new ArgumentOutOfRangeException(nameof(evaluatedAtUtc));

        if (FreshnessState is MobileHomeFreshnessState.Unknown or MobileHomeFreshnessState.Stale)
            return FreshnessState;

        if (FreshnessState != MobileHomeFreshnessState.Current || !FreshnessExpiresAtUtc.HasValue)
            return MobileHomeFreshnessState.Unknown;

        return FreshnessExpiresAtUtc.Value <= evaluatedAtUtc
            ? MobileHomeFreshnessState.Stale
            : MobileHomeFreshnessState.Current;
    }

    public bool IsValid =>
        (VerifiedDisplayName is null || (VerifiedDisplayName.Length is > 0 and <= 100)) &&
        (AuthorizedWorkspaceLabel is null || (AuthorizedWorkspaceLabel.Length is > 0 and <= 100)) &&
        (AuthorizedRegionLabel is null || (AuthorizedRegionLabel.Length is > 0 and <= 100)) &&
        AsOfUtc != default && Enum.IsDefined(FreshnessState) && Enum.IsDefined(Completeness) &&
        (FreshnessState switch
        {
            MobileHomeFreshnessState.Current => FreshnessExpiresAtUtc.HasValue && FreshnessExpiresAtUtc > AsOfUtc,
            MobileHomeFreshnessState.Stale => FreshnessExpiresAtUtc.HasValue && FreshnessExpiresAtUtc <= AsOfUtc,
            MobileHomeFreshnessState.Unknown => FreshnessExpiresAtUtc is null,
            _ => false
        }) &&
        WorkCounts is not null && RecentActivity is not null &&
        WorkCounts.Count == Enum.GetValues<MobileHomeWorkCountKind>().Length &&
        Enum.GetValues<MobileHomeWorkCountKind>().All(kind => WorkCounts.Count(item => item.Kind == kind) == 1) &&
        WorkCounts.All(item => item.IsValid) && RecentActivity.Count <= 5 &&
        RecentActivity.All(item => item.IsValid && item.OccurredAtUtc <= AsOfUtc) &&
        RecentActivity.Select(item => item.OccurredAtUtc).SequenceEqual(
            RecentActivity.Select(item => item.OccurredAtUtc).OrderByDescending(value => value)) &&
        HasConsistentFollowUpCounts();

    private bool HasConsistentFollowUpCounts()
    {
        var due = WorkCounts.First(item => item.Kind == MobileHomeWorkCountKind.DueFollowUps);
        var overdue = WorkCounts.First(item => item.Kind == MobileHomeWorkCountKind.OverdueFollowUps);

        if (due.State != MobileHomeValueState.Available)
            return overdue.State == due.State && overdue.Reason == due.Reason;

        if (overdue.State != MobileHomeValueState.Available)
            return true;

        return due.Value.HasValue && overdue.Value.HasValue && overdue.Value.Value <= due.Value.Value;
    }
}

public interface IMobileHomeOverviewClient
{
    Task<UiCapabilityResult<MobileHomeOverview>> GetAsync(
        MobileHomeOverviewQuery query,
        CancellationToken cancellationToken = default);
}
