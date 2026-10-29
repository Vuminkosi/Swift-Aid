namespace SwiftAid.Client.Presentation;

/// <summary>Closed work categories supported by the local practice workspace.</summary>
public enum WorkManagementKind
{
    Assignment,
    Assistance,
    FollowUp,
    Escalation
}

/// <summary>Closed work states supported by the local practice workspace.</summary>
public enum WorkManagementStatus
{
    Open,
    Pending,
    AcknowledgementPending,
    Accepted,
    InProgress,
    Provided,
    ReceiptConfirmed,
    Unfulfilled,
    Assigned,
    Acknowledged,
    Requested,
    Scheduled,
    Rescheduled,
    Missed,
    Unreachable,
    Cancelled,
    Completed,
    Resolved,
    NeedsReview,
    Unavailable,
    ActionRecorded
}

/// <summary>Assignment relationship for a practice work item.</summary>
public enum WorkManagementAssignmentStatus
{
    Unassigned,
    AcknowledgementPending,
    AssignedToYou,
    Assigned,
    Unavailable
}

/// <summary>Closed work actions accepted by the practice workspace.</summary>
public enum WorkManagementAction
{
    AssignWorker,
    ReassignWorker,
    RequestAssistance,
    RecordAssistanceProvided,
    ConfirmAssistanceReceived,
    RecordAssistanceUnmet,
    ScheduleFollowUp,
    RescheduleFollowUp,
    CancelFollowUp,
    RecordFollowUpOutcome,
    RequestEscalation,
    AcknowledgeEscalation,
    RecordEscalationAction,
    ResolveEscalation,
    AcknowledgeAssignment
}

public enum WorkManagementAssistanceNeed
{
    Transport,
    Supplies,
    SpecialistSupport,
    Other
}

public enum WorkManagementFollowUpOutcome
{
    ContactMade,
    NoContact,
    NeedsAnotherFollowUp,
    ReferredForReview
}

public enum WorkManagementEscalationReason
{
    NeedsSupervisorReview,
    ServiceBarrier,
    TimeSensitive,
    Other
}

public enum WorkManagementEscalationAction
{
    Reviewed,
    CoordinatedWithTeam,
    ResourceRequestRecorded,
    NoActionTaken
}

/// <summary>Bounded filters and paging for one case's work list.</summary>
public sealed record WorkManagementQuery(
    PageRequest Page,
    WorkManagementKind? Kind = null,
    WorkManagementStatus? Status = null,
    bool? OverdueOnly = null)
{
    public bool IsValid =>
        Page is { IsValid: true, PageNumber: <= 100_000 } &&
        (Kind is null || Enum.IsDefined(Kind.Value)) &&
        (Status is null || Enum.IsDefined(Status.Value));
}

public sealed record WorkManagementWorkerOption(string Key, string DisplayName);

/// <summary>Redacted work history; free-text action details are never projected.</summary>
public sealed record WorkManagementHistoryEntry(
    WorkManagementAction Action,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? DueAtUtc = null,
    DateTimeOffset? NewDueAtUtc = null,
    string? LocalTimeZoneId = null,
    TimeSpan? LocalOffset = null,
    string? CancellationReason = null,
    string? Reason = null,
    string? ResolutionSummary = null,
    WorkManagementAssistanceNeed? AssistanceNeed = null,
    bool AssistanceReceiptConfirmed = false,
    WorkManagementFollowUpOutcome? FollowUpOutcome = null,
    WorkManagementEscalationReason? EscalationReason = null,
    WorkManagementEscalationAction? EscalationAction = null,
    string? AssignedWorkerKey = null,
    string? AssignedWorkerLabel = null,
    string? ActorLabel = null);

/// <summary>Minimum-necessary work item for a case coordination view.</summary>
public sealed record WorkManagementItem(
    string WorkKey,
    string DisplayReference,
    WorkManagementKind Kind,
    WorkManagementStatus Status,
    WorkManagementAssignmentStatus Assignment,
    string Summary,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DueAtUtc,
    IReadOnlyList<WorkManagementAction> AvailableActions,
    IReadOnlyList<WorkManagementHistoryEntry> History,
    IReadOnlyList<WorkManagementWorkerOption> EligibleWorkers,
    string? AssignedWorkerKey = null,
    string? AssignedWorkerLabel = null,
    bool CanAcknowledgeAssignment = false);

public sealed record WorkManagementSnapshot(
    Guid CaseId,
    long Version,
    Page<WorkManagementItem> Items);

/// <summary>Typed command for one available work action; values are validated per action.</summary>
public sealed record WorkManagementCommand(
    Guid CaseId,
    string WorkKey,
    WorkManagementAction Action,
    long ExpectedVersion,
    string? WorkerKey = null,
    WorkManagementAssistanceNeed? AssistanceNeed = null,
    string? Details = null,
    WorkManagementFollowUpOutcome? FollowUpOutcome = null,
    WorkManagementEscalationReason? EscalationReason = null,
    WorkManagementEscalationAction? EscalationAction = null,
    bool AssistanceReceiptConfirmed = false,
    DateTimeOffset? DueAtUtc = null,
    DateTimeOffset? NewDueAtUtc = null,
    string? LocalTimeZoneId = null,
    TimeSpan? LocalOffset = null,
    string? CancellationReason = null,
    string? Reason = null,
    string? ResolutionSummary = null)
{
    public bool IsValid =>
        CaseId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(WorkKey) &&
        WorkKey.Length <= 120 &&
        ExpectedVersion > 0 &&
        Enum.IsDefined(Action) &&
        HasValidPayload();

    private bool HasValidPayload() => Action switch
    {
        WorkManagementAction.AssignWorker or WorkManagementAction.ReassignWorker =>
            HasText(WorkerKey, 100) && IsEmptyExcept(nameof(WorkerKey)),
        WorkManagementAction.AcknowledgeAssignment => IsEmptyExcept(),
        WorkManagementAction.RequestAssistance =>
            AssistanceNeed is { } need && Enum.IsDefined(need) && HasText(Details, 300) && IsEmptyExcept(nameof(AssistanceNeed), nameof(Details)),
        WorkManagementAction.RecordAssistanceProvided or WorkManagementAction.RecordAssistanceUnmet =>
            AssistanceNeed is { } supportNeed && Enum.IsDefined(supportNeed) &&
            HasText(Details, 300) && IsEmptyExcept(nameof(AssistanceNeed), nameof(Details)),
        WorkManagementAction.ConfirmAssistanceReceived =>
            AssistanceReceiptConfirmed && IsEmptyExcept(nameof(AssistanceReceiptConfirmed)),
        WorkManagementAction.ScheduleFollowUp =>
            HasUtc(DueAtUtc) && HasLocalSchedule() && IsEmptyExcept(nameof(DueAtUtc), nameof(LocalTimeZoneId), nameof(LocalOffset)),
        WorkManagementAction.RescheduleFollowUp =>
            HasUtc(DueAtUtc) && HasUtc(NewDueAtUtc) && HasLocalSchedule() && IsEmptyExcept(nameof(DueAtUtc), nameof(NewDueAtUtc), nameof(LocalTimeZoneId), nameof(LocalOffset)),
        WorkManagementAction.CancelFollowUp =>
            HasUtc(DueAtUtc) && HasText(CancellationReason, 300) && IsEmptyExcept(nameof(DueAtUtc), nameof(CancellationReason)),
        WorkManagementAction.RecordFollowUpOutcome =>
            FollowUpOutcome is { } outcome && Enum.IsDefined(outcome) && HasText(Details, 300) && IsEmptyExcept(nameof(Details), nameof(FollowUpOutcome)),
        WorkManagementAction.RequestEscalation =>
            EscalationReason is { } escalationReason && Enum.IsDefined(escalationReason) && HasText(Reason, 300) && IsEmptyExcept(nameof(EscalationReason), nameof(Reason)),
        WorkManagementAction.AcknowledgeEscalation => IsEmptyExcept(),
        WorkManagementAction.RecordEscalationAction =>
            EscalationAction is { } escalationAction && Enum.IsDefined(escalationAction) && HasText(Details, 300) && IsEmptyExcept(nameof(Details), nameof(EscalationAction)),
        WorkManagementAction.ResolveEscalation =>
            HasText(Reason, 300) && HasText(ResolutionSummary, 300) && IsEmptyExcept(nameof(Reason), nameof(ResolutionSummary)),
        _ => false
    };

    private bool HasLocalSchedule() =>
        !string.IsNullOrWhiteSpace(LocalTimeZoneId) &&
        LocalTimeZoneId.Length <= 100 &&
        LocalOffset is { } offset &&
        offset >= TimeSpan.FromHours(-14) &&
        offset <= TimeSpan.FromHours(14);

    private bool IsEmptyExcept(params string[] allowed) =>
        (WorkerKey is null || allowed.Contains(nameof(WorkerKey), StringComparer.Ordinal)) &&
        (AssistanceNeed is null || allowed.Contains(nameof(AssistanceNeed), StringComparer.Ordinal)) &&
        (Details is null || allowed.Contains(nameof(Details), StringComparer.Ordinal)) &&
        (FollowUpOutcome is null || allowed.Contains(nameof(FollowUpOutcome), StringComparer.Ordinal)) &&
        (EscalationReason is null || allowed.Contains(nameof(EscalationReason), StringComparer.Ordinal)) &&
        (EscalationAction is null || allowed.Contains(nameof(EscalationAction), StringComparer.Ordinal)) &&
        (!AssistanceReceiptConfirmed || allowed.Contains(nameof(AssistanceReceiptConfirmed), StringComparer.Ordinal)) &&
        (DueAtUtc is null || allowed.Contains(nameof(DueAtUtc), StringComparer.Ordinal)) &&
        (NewDueAtUtc is null || allowed.Contains(nameof(NewDueAtUtc), StringComparer.Ordinal)) &&
        (LocalTimeZoneId is null || allowed.Contains(nameof(LocalTimeZoneId), StringComparer.Ordinal)) &&
        (LocalOffset is null || allowed.Contains(nameof(LocalOffset), StringComparer.Ordinal)) &&
        (CancellationReason is null || allowed.Contains(nameof(CancellationReason), StringComparer.Ordinal)) &&
        (Reason is null || allowed.Contains(nameof(Reason), StringComparer.Ordinal)) &&
        (ResolutionSummary is null || allowed.Contains(nameof(ResolutionSummary), StringComparer.Ordinal));

    private static bool HasText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength;

    private static bool HasUtc(DateTimeOffset? value) => value is { Offset: var offset } && offset == TimeSpan.Zero;
}

/// <summary>In-memory practice operations for assignment, assistance, follow-up, and escalation.</summary>
public interface IWorkManagementClient
{
    Task<UiCapabilityResult<WorkManagementSnapshot>> GetAsync(
        Guid caseId,
        WorkManagementQuery query,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<WorkManagementSnapshot>> ExecuteAsync(
        WorkManagementCommand command,
        WorkManagementQuery query,
        CancellationToken cancellationToken = default);
}
