using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Presentation;

/// <summary>
/// Session-local sample data shared by the fake case, work, and coordination clients.
/// This store has no persistence or service dependencies.
/// </summary>
public sealed class FakeCaseStore
{
    private static readonly Guid CaseOneId = Guid.Parse("a5a8e6ca-83b0-4b75-8ec8-ecab1e680101");
    private static readonly Guid CaseTwoId = Guid.Parse("a5a8e6ca-83b0-4b75-8ec8-ecab1e680102");
    private static readonly Guid ReceiptOneId = Guid.Parse("b6b9f7db-94c1-4c86-9fd9-fdcb2f791101");
    private static readonly Guid ReceiptTwoId = Guid.Parse("b6b9f7db-94c1-4c86-9fd9-fdcb2f791102");
    private const string SampleNotice = "";
    private readonly object _gate = new();
    private readonly DateTimeOffset _sessionStartedAtUtc = DateTimeOffset.UtcNow;
    private readonly Dictionary<Guid, FakeCaseState> _cases;
    private readonly Dictionary<Guid, FakeCoordinationState> _coordinationReports;
    private readonly IReadOnlyList<IntakeCoordinationQueueOption> _coordinationQueues;

    public FakeCaseStore()
    {
        _cases = CreateCases(_sessionStartedAtUtc);
        _coordinationQueues =
        [
            new IntakeCoordinationQueueOption("sample-harare-queue", "Community organization A", "ZW", "sample-country-catalogue-v1"),
            new IntakeCoordinationQueueOption("sample-bulawayo-queue", "Community organization B", "ZW", "sample-country-catalogue-v1")
        ];
        _coordinationReports = CreateCoordinationReports(_sessionStartedAtUtc);
    }

    internal CaseQueueResult GetMine(int? limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is <= 0)
        {
            return new CaseQueueResult(CaseQueueAccess.Unavailable, []);
        }

        lock (_gate)
        {
            var items = _cases.Values
                .OrderByDescending(item => item.UpdatedAtUtc)
                .Take(Math.Min(limit ?? 100, 100))
                .Select(ToQueueItem)
                .ToArray();
            return new CaseQueueResult(CaseQueueAccess.Allowed, items);
        }
    }

    internal CaseTimelineResult GetTimeline(Guid caseId, int? limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty || limit is <= 0)
        {
            return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        }

        lock (_gate)
        {
            if (!_cases.TryGetValue(caseId, out var state))
            {
                return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
            }

            var entries = state.Timeline
                .TakeLast(Math.Min(limit ?? 100, 100))
                .ToArray();
            return new CaseTimelineResult(CaseTimelineAccess.Found, ToTimeline(state, entries));
        }
    }

    internal MonitoringQueueResult GetMonitoring(bool assignedOnly, int? limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is <= 0)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Unavailable, []);
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var work = _cases.Values
                .SelectMany(state => state.WorkItems.Select(item => (Case: state, Work: item)))
                .Where(pair => pair.Work.Kind is WorkManagementKind.FollowUp or WorkManagementKind.Escalation)
                .Where(pair => pair.Work.Status is not (WorkManagementStatus.Completed or WorkManagementStatus.Cancelled or WorkManagementStatus.Resolved or WorkManagementStatus.Unavailable))
                .Where(pair => pair.Work.Kind != WorkManagementKind.Escalation || pair.Work.Status != WorkManagementStatus.Pending)
                .Where(pair => !assignedOnly || pair.Work.Assignment is WorkManagementAssignmentStatus.Assigned or WorkManagementAssignmentStatus.AssignedToYou)
                .OrderBy(pair => pair.Work.DueAtUtc ?? DateTimeOffset.MaxValue)
                .Take(Math.Min(limit ?? 100, 100))
                .Select(pair => new MonitoringQueueItem(
                    pair.Case.Id,
                    pair.Work.Kind == WorkManagementKind.FollowUp ? MonitoringQueueKind.FollowUp : MonitoringQueueKind.Escalation,
                    pair.Work.Id,
                    pair.Work.Summary,
                    pair.Work.Assignment is WorkManagementAssignmentStatus.Assigned or WorkManagementAssignmentStatus.AssignedToYou
                        ? "Coordinator"
                        : null,
                    pair.Work.DueAtUtc,
                    pair.Work.Status.ToString(),
                    pair.Work.DueAtUtc is { } due && due < now,
                    pair.Work.UpdatedAtUtc,
                    pair.Case.Version))
                .ToArray();
            return new MonitoringQueueResult(CaseQueueAccess.Allowed, work);
        }
    }

    internal CaseCommandResult SendCaseCommand(
        Guid caseId,
        CaseCommandInput? input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty || input is null)
        {
            return new CaseCommandResult(CaseCommandStatus.Invalid, Guid.Empty, [], null, null);
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_cases.TryGetValue(caseId, out var state))
            {
                return new CaseCommandResult(CaseCommandStatus.Denied, Guid.Empty, [], null, null);
            }

            if (input.ExpectedVersion is < 0)
            {
                return new CaseCommandResult(CaseCommandStatus.Invalid, Guid.Empty, [], null, null);
            }

            if (input.ExpectedVersion is { } expectedVersion && expectedVersion != state.Version)
            {
                return new CaseCommandResult(CaseCommandStatus.Conflict, Guid.NewGuid(), [], state.Version, state.ReportRevision);
            }

            var commandId = Guid.NewGuid();
            var type = input.CommandType;
            var reason = input.Reason;
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            {
                return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
            }

            string eventType;
            var nextStatus = state.Workflow;
            var reportRevision = state.ReportRevision;

            switch (type)
            {
                case "CorrectReport":
                    eventType = "ReportCorrected";
                    reportRevision++;
                    break;
                case "RequestProtectedAction":
                    if (!TryParseEnum(input.Action, out CaseAction requestedAction) ||
                        requestedAction is not (CaseAction.Reassign or CaseAction.Assist) ||
                        string.IsNullOrWhiteSpace(input.ProposedOwnerId))
                    {
                        return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
                    }

                    var requestId = input.RequestId is { } providedRequestId && providedRequestId != Guid.Empty
                        ? providedRequestId
                        : Guid.NewGuid();
                    if (state.ActionRequests.ContainsKey(requestId))
                    {
                        return new CaseCommandResult(CaseCommandStatus.Conflict, commandId, [], state.Version, state.ReportRevision);
                    }

                    state.ActionRequests.Add(requestId, new FakeActionRequest(requestedAction, isPending: true));
                    eventType = "CaseActionRequested";
                    if (requestedAction == CaseAction.Assist)
                    {
                        AddRequestedAssistance(state, commandId);
                    }
                    break;
                case "ReviewProtectedAction":
                    if (!TryParseEnum(input.Outcome, out CaseActionReviewOutcome reviewOutcome))
                    {
                        return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
                    }

                    var request = FindActionRequest(state, input.RequestId);
                    if (request is null || !request.IsPending)
                    {
                        return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
                    }

                    request.IsPending = false;
                    request.Outcome = reviewOutcome;
                    eventType = "CaseActionRequestReviewed";
                    break;
                case "Resolve":
                    if (state.Workflow == "Resolved")
                    {
                        return new CaseCommandResult(CaseCommandStatus.Conflict, commandId, [], state.Version, state.ReportRevision);
                    }

                    nextStatus = "Resolved";
                    eventType = "CaseResolved";
                    break;
                case "Reopen":
                    if (string.IsNullOrWhiteSpace(input.ProposedOwnerId) || state.Workflow != "Resolved")
                    {
                        return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
                    }

                    nextStatus = "Active";
                    eventType = "CaseReopened";
                    break;
                case "RecordObservation":
                    if (input.ObservationId is null || input.ObservationId.GetValueOrDefault() == Guid.Empty ||
                        string.IsNullOrWhiteSpace(input.Narrative) || input.Narrative.Length > 1000 ||
                        string.IsNullOrWhiteSpace(input.Source) || input.Source.Length > 200)
                    {
                        return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
                    }

                    eventType = "CaseObservationRecorded";
                    break;
                case "ScheduleFollowUp":
                    if (input.FollowUpId is not { } followUpId || followUpId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(input.Purpose) || input.Purpose.Length > 200 ||
                        string.IsNullOrWhiteSpace(input.AssigneeActorId) || input.AssigneeActorId.Length > 100 ||
                        input.DueAtUtc is not { } dueAtUtc || dueAtUtc.Offset != TimeSpan.Zero ||
                        FindWorkItem(state, followUpId) is not null)
                    {
                        return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
                    }

                    state.WorkItems.Add(CreateFollowUp(followUpId, dueAtUtc, state, WorkManagementStatus.Scheduled));
                    nextStatus = "AwaitingFollowUp";
                    eventType = "CaseFollowUpScheduled";
                    break;
                case "RecordFollowUpOutcome":
                    if (input.FollowUpId is not { } outcomeId || outcomeId == Guid.Empty ||
                        !TryParseEnum(input.OutcomeName, out FollowUpOutcome followUpOutcome) ||
                        string.IsNullOrWhiteSpace(input.OutcomeNotes) || input.OutcomeNotes.Length > 500 ||
                        (followUpOutcome == FollowUpOutcome.Rescheduled
                            ? !IsUtc(input.NewDueAtUtc)
                            : input.NewDueAtUtc is not null))
                    {
                        return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
                    }

                    var followUp = FindWorkItem(state, outcomeId);
                    if (followUp is null || followUp.Kind != WorkManagementKind.FollowUp ||
                        followUp.Status is WorkManagementStatus.Completed or WorkManagementStatus.Cancelled or WorkManagementStatus.Unavailable ||
                        (followUpOutcome == FollowUpOutcome.Rescheduled && input.NewDueAtUtc is null))
                    {
                        return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
                    }

                    followUp.DueAtUtc = followUpOutcome == FollowUpOutcome.Rescheduled ? input.NewDueAtUtc : followUp.DueAtUtc;
                    followUp.Status = followUpOutcome switch
                    {
                        FollowUpOutcome.Completed => WorkManagementStatus.Completed,
                        FollowUpOutcome.Rescheduled => WorkManagementStatus.Rescheduled,
                        FollowUpOutcome.Cancelled => WorkManagementStatus.Cancelled,
                        FollowUpOutcome.Missed => WorkManagementStatus.Missed,
                        FollowUpOutcome.Unreachable => WorkManagementStatus.Unreachable,
                        _ => WorkManagementStatus.Open
                    };
                    nextStatus = followUpOutcome switch
                    {
                        FollowUpOutcome.Rescheduled or FollowUpOutcome.Missed or FollowUpOutcome.Unreachable => "AwaitingFollowUp",
                        _ => "Active"
                    };
                    followUp.AvailableActions = followUp.Status is WorkManagementStatus.Completed or WorkManagementStatus.Cancelled or WorkManagementStatus.Missed or WorkManagementStatus.Unreachable
                        ? []
                        : [WorkManagementAction.RescheduleFollowUp, WorkManagementAction.CancelFollowUp, WorkManagementAction.RecordFollowUpOutcome];
                    followUp.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    eventType = "CaseFollowUpOutcomeRecorded";
                    break;
                case "RaiseEscalation":
                    if (input.EscalationId is not { } escalationId || escalationId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(input.RecipientRoute) || input.RecipientRoute.Length > 100 ||
                        (input.EvidenceEventIds?.Any(string.IsNullOrWhiteSpace) ?? false) ||
                        FindWorkItem(state, escalationId) is not null)
                    {
                        return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
                    }

                    state.WorkItems.Add(CreateEscalation(escalationId, state, WorkManagementStatus.Open));
                    eventType = "CaseEscalationRaised";
                    break;
                case "AcknowledgeEscalation":
                    if (input.EscalationId is not { } acknowledgeId ||
                        FindWorkItem(state, acknowledgeId) is not { Kind: WorkManagementKind.Escalation } escalationToAcknowledge ||
                        escalationToAcknowledge.Status != WorkManagementStatus.Open)
                    {
                        return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
                    }

                    escalationToAcknowledge.Status = WorkManagementStatus.Acknowledged;
                    escalationToAcknowledge.AvailableActions = [WorkManagementAction.RecordEscalationAction, WorkManagementAction.ResolveEscalation];
                    escalationToAcknowledge.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    eventType = "CaseEscalationAcknowledged";
                    break;
                case "CloseEscalation":
                    if (input.EscalationId is not { } closeId || string.IsNullOrWhiteSpace(input.Resolution) || input.Resolution.Length > 500 ||
                        FindWorkItem(state, closeId) is not { Kind: WorkManagementKind.Escalation } escalationToClose ||
                        escalationToClose.Status is not (WorkManagementStatus.Open or WorkManagementStatus.Acknowledged))
                    {
                        return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
                    }

                    escalationToClose.Status = WorkManagementStatus.Resolved;
                    escalationToClose.AvailableActions = [];
                    escalationToClose.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    eventType = "CaseEscalationClosed";
                    break;
                default:
                    return new CaseCommandResult(CaseCommandStatus.Invalid, commandId, [], null, null);
            }

            state.Workflow = nextStatus;
            state.ReportRevision = reportRevision;
            var eventId = AddTimelineEntry(state, eventType, commandId);
            return new CaseCommandResult(CaseCommandStatus.Accepted, commandId, [eventId], state.Version, state.ReportRevision);
        }
    }

    internal UiCapabilityResult<WorkManagementSnapshot> GetWorkManagement(
        Guid caseId,
        WorkManagementQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null || !query.IsValid)
        {
            return Result(UiCapabilityResultCode.InvalidRequest, null, "Check the work filters and try again.");
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_cases.TryGetValue(caseId, out var state))
            {
                return Result(UiCapabilityResultCode.Denied, null, "This item is not available.");
            }

            return Result(UiCapabilityResultCode.Success, CreateWorkManagementSnapshot(state, query));
        }
    }

    internal UiCapabilityResult<WorkManagementSnapshot> ApplyWorkManagement(
        WorkManagementCommand command,
        WorkManagementQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command is null || query is null || !query.IsValid || !command.IsValid)
        {
            return Result(UiCapabilityResultCode.InvalidRequest, null, "Check the work action and details.");
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_cases.TryGetValue(command.CaseId, out var state))
            {
                return Result(UiCapabilityResultCode.Denied, null, "This item is not available.");
            }

            if (state.Version != command.ExpectedVersion)
            {
                return Result(UiCapabilityResultCode.Conflict, CreateWorkManagementSnapshot(state, query), "This item changed. Refresh and try again.");
            }

            var work = state.WorkItems.FirstOrDefault(item => string.Equals(item.WorkKey, command.WorkKey, StringComparison.Ordinal));
            if (work is null || !work.AvailableActions.Contains(command.Action))
            {
                return Result(UiCapabilityResultCode.Conflict, CreateWorkManagementSnapshot(state, query), "That action is no longer available. Refresh and try again.");
            }

            if (!TryApplyWorkAction(state, work, command))
            {
                return Result(UiCapabilityResultCode.InvalidRequest, null, "Check the action details and try again.");
            }

            var commandId = Guid.NewGuid();
            AddTimelineEntry(state, TimelineEventFor(command.Action), commandId);
            work.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return Result(UiCapabilityResultCode.Success, CreateWorkManagementSnapshot(state, query), "Action completed.");
        }
    }

    internal (CoordinationQueueAccess Access, IReadOnlyList<IntakeCoordinationQueueEntry> Entries) GetCoordinationQueue(
        IntakeRoutingStatus? routingStatus,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100 || routingStatus is { } status && !Enum.IsDefined(status))
        {
            return (CoordinationQueueAccess.Unavailable, []);
        }

        lock (_gate)
        {
            var entries = _coordinationReports.Values
                .Select(item => item.Entry)
                .Where(item => IsCoordinationEligible(item))
                .Where(item => routingStatus is null || item.RoutingStatus == routingStatus)
                .OrderByDescending(item => item.ReceivedAtUtc)
                .Take(limit)
                .ToArray();
            return (CoordinationQueueAccess.Allowed, entries);
        }
    }

    internal IntakeCoordinationDetail? GetCoordinationDetail(Guid receiptId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return _coordinationReports.TryGetValue(receiptId, out var report)
                && IsCoordinationEligible(report.Entry)
                ? new IntakeCoordinationDetail(report.Entry, report.History.ToArray())
                : null;
        }
    }

    internal IReadOnlyList<IntakeCoordinationQueueOption> GetCoordinationQueues(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _coordinationQueues.ToArray();
    }

    internal CoordinationActionOutcome ActOnCoordination(
        Guid receiptId,
        long expectedVersion,
        IntakeCoordinationAction action,
        string? targetQueueId,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receiptId == Guid.Empty || expectedVersion < 1 || !Enum.IsDefined(action) ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            return CoordinationActionOutcome.Invalid;
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_coordinationReports.TryGetValue(receiptId, out var report))
            {
                return CoordinationActionOutcome.NotFound;
            }

            var current = report.Entry;
            var before = current;
            if (current.Version != expectedVersion)
            {
                return CoordinationActionOutcome.Conflict;
            }

            if (!IsCoordinationEligible(current))
            {
                return CoordinationActionOutcome.NotFound;
            }

            if (action == IntakeCoordinationAction.AssignQueue &&
                (string.IsNullOrWhiteSpace(targetQueueId) || !_coordinationQueues.Any(queue => queue.QueueId == targetQueueId && queue.CountryCode == current.CountryCode)))
            {
                return CoordinationActionOutcome.Invalid;
            }

            if (action != IntakeCoordinationAction.AssignQueue && targetQueueId is not null)
            {
                return CoordinationActionOutcome.Invalid;
            }

            switch (action)
            {
                case IntakeCoordinationAction.ClaimForReview when current.CoordinatorActorId is null:
                    current = current with { CoordinatorActorId = "sample-coordinator", Version = current.Version + 1 };
                    break;
                case IntakeCoordinationAction.AssignQueue when current.RoutingStatus == IntakeRoutingStatus.NeedsRouting:
                    current = current with
                    {
                        Status = IntakeStatus.AwaitingReview,
                        RoutingStatus = IntakeRoutingStatus.RoutedForReview,
                        IntakeQueueId = targetQueueId,
                        Version = current.Version + 1
                    };
                    break;
                case IntakeCoordinationAction.RequestClarification when !current.ClarificationRequested:
                    current = current with { ClarificationRequested = true, Version = current.Version + 1 };
                    break;
                case IntakeCoordinationAction.RecordNoServiceCoverage when current.RoutingStatus == IntakeRoutingStatus.NeedsRouting:
                    current = current with
                    {
                        RoutingStatus = IntakeRoutingStatus.NoServiceCoverage,
                        Version = current.Version + 1
                    };
                    break;
                default:
                    return CoordinationActionOutcome.Invalid;
            }

            report.Entry = current;
            report.History.Add(new IntakeCoordinationActionRecord(
                Guid.NewGuid(),
                action,
                "sample-coordinator",
                "sample-organization",
                "sample-coordinator-workspace",
                reason.Trim(),
                before.Status,
                before.RoutingStatus,
                before.IntakeQueueId,
                current.Status,
                current.RoutingStatus,
                current.IntakeQueueId,
                current.Version,
                DateTimeOffset.UtcNow));
            return CoordinationActionOutcome.Applied;
        }
    }

    internal static UiCapabilityMetadata WorkMetadata() => UiCapabilityResults.SyntheticMetadata("work-management", SampleNotice);

    internal IReadOnlyList<CaseWorkspaceSeed> GetWorkspaceSeeds(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return _cases.Values
                .Select(state =>
                {
                    var isFirst = state.Id == CaseOneId;
                    return new CaseWorkspaceSeed(
                        ToQueueItem(state),
                        isFirst ? CaseWorkspaceSector.Human : CaseWorkspaceSector.Animal,
                        "Zimbabwe",
                        isFirst ? "sample-harare-region" : "sample-bulawayo-region",
                        isFirst ? "Harare" : "Bulawayo",
                        isFirst ? Workers[1].Key : Workers[0].Key,
                        isFirst ? Workers[1].DisplayName : Workers[0].DisplayName,
                        isFirst ? "coordination" : "access-to-support",
                        isFirst ? "Coordination" : "Access to support",
                        isFirst ? "SAMPLE-A01" : "SAMPLE-B02");
                })
                .OrderByDescending(seed => seed.Case.UpdatedAtUtc)
                .ToArray();
        }
    }

    private UiCapabilityResult<WorkManagementSnapshot> Result(
        UiCapabilityResultCode code,
        WorkManagementSnapshot? value,
        string? message = null) =>
        new(code, value, WorkMetadata(), message);

    private WorkManagementSnapshot CreateWorkManagementSnapshot(FakeCaseState state, WorkManagementQuery query)
    {
        var now = DateTimeOffset.UtcNow;
        var filtered = state.WorkItems
            .Where(item => query.Kind is null || item.Kind == query.Kind)
            .Where(item => query.Status is null || item.Status == query.Status)
            .Where(item => query.OverdueOnly is null ||
                (item.DueAtUtc is { } due && due < now) == query.OverdueOnly.Value)
            .OrderBy(item => item.DueAtUtc ?? DateTimeOffset.MaxValue)
            .Select(ToWorkManagementItem)
            .ToArray();
        var skip = (query.Page.PageNumber - 1) * query.Page.PageSize;
        var pageItems = filtered.Skip(skip).Take(query.Page.PageSize).ToArray();
        return new WorkManagementSnapshot(
            state.Id,
            state.Version,
            new Page<WorkManagementItem>(
                pageItems,
                query.Page.PageNumber,
                query.Page.PageSize,
                filtered.Length,
                skip + pageItems.Length < filtered.Length));
    }

    private bool TryApplyWorkAction(FakeCaseState state, FakeWorkItemState work, WorkManagementCommand command)
    {
        var occurredAtUtc = DateTimeOffset.UtcNow;
        var priorDue = work.DueAtUtc;
        var nextStatus = work.Status;
        var historyWork = work;
        DateTimeOffset? newDue = null;
        switch (command.Action)
        {
            case WorkManagementAction.AssignWorker:
            case WorkManagementAction.ReassignWorker:
                if (work.Kind != WorkManagementKind.Assignment || !TryGetWorker(command.WorkerKey, out _)) return false;
                work.Assignment = WorkManagementAssignmentStatus.Assigned;
                work.Status = WorkManagementStatus.Assigned;
                work.AvailableActions = [WorkManagementAction.ReassignWorker, WorkManagementAction.RequestAssistance, WorkManagementAction.RequestEscalation];
                break;
            case WorkManagementAction.RequestAssistance:
                if (work.Kind is not (WorkManagementKind.Assignment or WorkManagementKind.Assistance) ||
                    command.AssistanceNeed is not { } need || !Enum.IsDefined(need) || RequiredText(command.Details, 300) is null)
                {
                    return false;
                }

                if (work.Kind == WorkManagementKind.Assignment)
                {
                    var assistance = state.WorkItems.FirstOrDefault(item => item.Kind == WorkManagementKind.Assistance && item.Status is not (WorkManagementStatus.ReceiptConfirmed or WorkManagementStatus.Cancelled));
                    if (assistance is null)
                    {
                        assistance = CreateAssistance(Guid.NewGuid(), state, WorkManagementStatus.Requested);
                        state.WorkItems.Add(assistance);
                    }

                    assistance.AssistanceNeed = need;
                    assistance.Status = WorkManagementStatus.Requested;
                    assistance.AvailableActions = [WorkManagementAction.RecordAssistanceProvided, WorkManagementAction.RecordAssistanceUnmet];
                    assistance.UpdatedAtUtc = occurredAtUtc;
                    historyWork = assistance;
                    nextStatus = work.Status;
                }
                else
                {
                    work.AssistanceNeed = need;
                    work.Status = WorkManagementStatus.Requested;
                    work.AssistanceProvided = false;
                    work.AssistanceReceived = false;
                    work.AvailableActions = [WorkManagementAction.RecordAssistanceProvided, WorkManagementAction.RecordAssistanceUnmet];
                }
                break;
            case WorkManagementAction.RecordAssistanceProvided:
                if (work.Kind != WorkManagementKind.Assistance ||
                    command.AssistanceNeed is not { } providedNeed || !Enum.IsDefined(providedNeed) ||
                    RequiredText(command.Details, 300) is null) return false;
                work.AssistanceNeed = providedNeed;
                work.AssistanceProvided = true;
                work.Status = WorkManagementStatus.Provided;
                work.AvailableActions = [WorkManagementAction.ConfirmAssistanceReceived];
                break;
            case WorkManagementAction.ConfirmAssistanceReceived:
                if (work.Kind != WorkManagementKind.Assistance || !work.AssistanceProvided || !command.AssistanceReceiptConfirmed) return false;
                work.AssistanceReceived = true;
                work.Status = WorkManagementStatus.ReceiptConfirmed;
                work.AvailableActions = [];
                break;
            case WorkManagementAction.RecordAssistanceUnmet:
                if (work.Kind != WorkManagementKind.Assistance ||
                    command.AssistanceNeed is not { } unmetNeed || !Enum.IsDefined(unmetNeed) ||
                    RequiredText(command.Details, 300) is null) return false;
                work.AssistanceNeed = unmetNeed;
                work.Status = WorkManagementStatus.Unfulfilled;
                work.AvailableActions = [WorkManagementAction.RequestAssistance];
                break;
            case WorkManagementAction.ScheduleFollowUp:
                if (work.Kind != WorkManagementKind.FollowUp || work.Status is WorkManagementStatus.Scheduled or WorkManagementStatus.Rescheduled or WorkManagementStatus.Completed or WorkManagementStatus.Cancelled ||
                    !TryValidateLocalSchedule(command, out var scheduledAt)) return false;
                newDue = scheduledAt;
                work.DueAtUtc = scheduledAt;
                work.Status = WorkManagementStatus.Scheduled;
                work.AvailableActions = [WorkManagementAction.RescheduleFollowUp, WorkManagementAction.CancelFollowUp, WorkManagementAction.RecordFollowUpOutcome];
                state.Workflow = "AwaitingFollowUp";
                break;
            case WorkManagementAction.RescheduleFollowUp:
                if (work.Kind != WorkManagementKind.FollowUp || work.Status is not (WorkManagementStatus.Scheduled or WorkManagementStatus.Rescheduled or WorkManagementStatus.Missed) ||
                    command.DueAtUtc != work.DueAtUtc || !TryValidateLocalSchedule(command, out var rescheduledAt) ||
                    command.NewDueAtUtc is not { } requestedNewDue || requestedNewDue.ToUniversalTime() != rescheduledAt) return false;
                newDue = rescheduledAt;
                work.DueAtUtc = rescheduledAt;
                work.Status = WorkManagementStatus.Rescheduled;
                state.Workflow = "AwaitingFollowUp";
                break;
            case WorkManagementAction.CancelFollowUp:
                if (work.Kind != WorkManagementKind.FollowUp || work.Status is not (WorkManagementStatus.Scheduled or WorkManagementStatus.Rescheduled or WorkManagementStatus.Missed) ||
                    command.DueAtUtc != work.DueAtUtc || RequiredText(command.CancellationReason, 300) is null) return false;
                work.Status = WorkManagementStatus.Cancelled;
                work.AvailableActions = [];
                state.Workflow = "Active";
                break;
            case WorkManagementAction.RecordFollowUpOutcome:
                if (work.Kind != WorkManagementKind.FollowUp || work.Status is not (WorkManagementStatus.Scheduled or WorkManagementStatus.Rescheduled or WorkManagementStatus.Missed) ||
                    command.FollowUpOutcome is not { } outcome || !Enum.IsDefined(outcome) || RequiredText(command.Details, 300) is null) return false;
                work.Status = outcome switch
                {
                    WorkManagementFollowUpOutcome.ContactMade => WorkManagementStatus.Completed,
                    WorkManagementFollowUpOutcome.NoContact => WorkManagementStatus.Missed,
                    WorkManagementFollowUpOutcome.NeedsAnotherFollowUp => WorkManagementStatus.Rescheduled,
                    WorkManagementFollowUpOutcome.ReferredForReview => WorkManagementStatus.NeedsReview,
                    _ => work.Status
                };
                state.Workflow = outcome switch
                {
                    WorkManagementFollowUpOutcome.NoContact => "AwaitingFollowUp",
                    WorkManagementFollowUpOutcome.NeedsAnotherFollowUp => "AwaitingFollowUp",
                    WorkManagementFollowUpOutcome.ReferredForReview => "UnderReview",
                    _ => "Active"
                };
                if (work.Status is WorkManagementStatus.Completed or WorkManagementStatus.Missed or WorkManagementStatus.Unreachable)
                {
                    work.AvailableActions = [];
                }
                break;
            case WorkManagementAction.RequestEscalation:
                if (work.Kind is not (WorkManagementKind.Assignment or WorkManagementKind.Escalation) ||
                    (work.Kind == WorkManagementKind.Escalation && work.Status != WorkManagementStatus.Pending) ||
                    command.EscalationReason is not { } escalationReason || !Enum.IsDefined(escalationReason) ||
                    RequiredText(command.Reason, 300) is null) return false;
                if (work.Kind == WorkManagementKind.Assignment)
                {
                    historyWork = CreateEscalation(Guid.NewGuid(), state, WorkManagementStatus.Open);
                    state.WorkItems.Add(historyWork);
                }
                else
                {
                    work.Status = WorkManagementStatus.Open;
                    work.AvailableActions = [WorkManagementAction.AcknowledgeEscalation, WorkManagementAction.ResolveEscalation];
                }
                historyWork.EscalationReason = escalationReason;
                break;
            case WorkManagementAction.AcknowledgeEscalation:
                if (work.Kind != WorkManagementKind.Escalation || work.Status != WorkManagementStatus.Open) return false;
                work.Status = WorkManagementStatus.Acknowledged;
                work.AvailableActions = [WorkManagementAction.RecordEscalationAction, WorkManagementAction.ResolveEscalation];
                break;
            case WorkManagementAction.RecordEscalationAction:
                if (work.Kind != WorkManagementKind.Escalation || work.Status != WorkManagementStatus.Acknowledged ||
                    command.EscalationAction is not { } escalationAction || !Enum.IsDefined(escalationAction) ||
                    RequiredText(command.Details, 300) is null) return false;
                work.AvailableActions = [WorkManagementAction.ResolveEscalation];
                break;
            case WorkManagementAction.ResolveEscalation:
                if (work.Kind != WorkManagementKind.Escalation || work.Status is not (WorkManagementStatus.Open or WorkManagementStatus.Acknowledged) ||
                    RequiredText(command.ResolutionSummary, 300) is null) return false;
                work.Status = WorkManagementStatus.Resolved;
                work.AvailableActions = [];
                break;
            default:
                return false;
        }

        historyWork.History.Add(new WorkManagementHistoryEntry(
            command.Action,
            occurredAtUtc,
            command.Action is WorkManagementAction.ScheduleFollowUp or WorkManagementAction.RescheduleFollowUp or WorkManagementAction.CancelFollowUp ? priorDue : null,
            command.Action is WorkManagementAction.ScheduleFollowUp or WorkManagementAction.RescheduleFollowUp ? newDue : null,
            command.LocalTimeZoneId,
            command.LocalOffset,
            command.Action == WorkManagementAction.CancelFollowUp ? command.CancellationReason?.Trim() : null,
            command.Action is WorkManagementAction.RequestEscalation or WorkManagementAction.ResolveEscalation ? command.Reason?.Trim() : null,
            command.Action == WorkManagementAction.ResolveEscalation ? command.ResolutionSummary?.Trim() : null,
            historyWork.Kind == WorkManagementKind.Assistance ? historyWork.AssistanceNeed : null,
            command.Action == WorkManagementAction.ConfirmAssistanceReceived && command.AssistanceReceiptConfirmed,
            command.Action == WorkManagementAction.RecordFollowUpOutcome ? command.FollowUpOutcome : null,
            command.Action == WorkManagementAction.RequestEscalation ? command.EscalationReason : null,
            command.Action == WorkManagementAction.RecordEscalationAction ? command.EscalationAction : null));
        historyWork.UpdatedAtUtc = occurredAtUtc;
        if (historyWork != work)
        {
            work.UpdatedAtUtc = occurredAtUtc;
        }
        if (work.Kind == WorkManagementKind.FollowUp && work.Status != nextStatus)
        {
            work.Assignment = WorkManagementAssignmentStatus.AssignedToYou;
        }

        state.UpdatedAtUtc = occurredAtUtc;
        return true;
    }

    private static bool TryValidateLocalSchedule(WorkManagementCommand command, out DateTimeOffset scheduledAt)
    {
        scheduledAt = default;
        var selectedDue = command.Action == WorkManagementAction.RescheduleFollowUp
            ? command.NewDueAtUtc
            : command.DueAtUtc;
        if (selectedDue is null || string.IsNullOrWhiteSpace(command.LocalTimeZoneId) || command.LocalOffset is not { } offset ||
            offset < TimeSpan.FromHours(-14) || offset > TimeSpan.FromHours(14))
        {
            return false;
        }

        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(command.LocalTimeZoneId);
            var localTime = DateTime.SpecifyKind(selectedDue.Value.ToOffset(offset).DateTime, DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(localTime))
            {
                return false;
            }

            if (timeZone.IsAmbiguousTime(localTime))
            {
                if (!timeZone.GetAmbiguousTimeOffsets(localTime).Contains(offset))
                {
                    return false;
                }
            }
            else if (timeZone.GetUtcOffset(localTime) != offset)
            {
                return false;
            }
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }

        scheduledAt = selectedDue.Value.ToUniversalTime();
        return true;
    }

    private static string? RequiredText(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength ? null : value.Trim();

    private static bool IsCoordinationEligible(IntakeCoordinationQueueEntry entry) =>
        entry.RoutingStatus is IntakeRoutingStatus.NeedsRouting or IntakeRoutingStatus.NoServiceCoverage;

    private static bool TryGetWorker(string? key, out WorkManagementWorkerOption worker)
    {
        worker = Workers.FirstOrDefault(item => item.Key == key)!;
        return worker is not null;
    }

    private static bool TryParseEnum<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out result) && Enum.IsDefined(result);

    private static bool IsUtc(DateTimeOffset? value) => value is { Offset: var offset } && offset == TimeSpan.Zero;

    private static FakeActionRequest? FindActionRequest(FakeCaseState state, Guid? requestId)
    {
        if (requestId is { } id)
        {
            return state.ActionRequests.GetValueOrDefault(id);
        }

        return state.ActionRequests.Values.FirstOrDefault(request => request.IsPending);
    }

    private static FakeWorkItemState? FindWorkItem(FakeCaseState state, Guid workItemId) =>
        state.WorkItems.FirstOrDefault(item => item.Id == workItemId);

    private static void AddRequestedAssistance(FakeCaseState state, Guid commandId)
    {
        var item = state.WorkItems.FirstOrDefault(work => work.Kind == WorkManagementKind.Assistance && work.Status != WorkManagementStatus.Completed);
        if (item is null)
        {
            item = CreateAssistance(Guid.NewGuid(), state, WorkManagementStatus.Requested);
            state.WorkItems.Add(item);
        }

        item.Status = WorkManagementStatus.Requested;
        item.AvailableActions = [WorkManagementAction.RecordAssistanceProvided, WorkManagementAction.RecordAssistanceUnmet];
        item.History.Add(new WorkManagementHistoryEntry(WorkManagementAction.RequestAssistance, DateTimeOffset.UtcNow, Reason: "Coordination requested"));
    }

    private static Guid AddTimelineEntry(FakeCaseState state, string eventType, Guid commandId)
    {
        state.Version++;
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        state.Timeline.Add(new CaseTimelineEntry(
            eventId,
            eventType,
            "Activity recorded",
            CaseTimelineEntryKind.Derived,
            state.Version,
            state.ReportRevision,
            commandId,
            "sample-staff",
            null,
            state.UpdatedAtUtc,
            1));
        return eventId;
    }

    private static string TimelineEventFor(WorkManagementAction action) => action switch
    {
        WorkManagementAction.AssignWorker or WorkManagementAction.ReassignWorker => "CaseActionRequested",
        WorkManagementAction.RequestAssistance => "CaseAssistanceRequested",
        WorkManagementAction.RecordAssistanceProvided or WorkManagementAction.ConfirmAssistanceReceived or WorkManagementAction.RecordAssistanceUnmet => "CaseObservationRecorded",
        WorkManagementAction.ScheduleFollowUp or WorkManagementAction.RescheduleFollowUp or WorkManagementAction.CancelFollowUp => "CaseFollowUpScheduled",
        WorkManagementAction.RecordFollowUpOutcome => "CaseFollowUpOutcomeRecorded",
        WorkManagementAction.RequestEscalation => "CaseEscalationRaised",
        WorkManagementAction.AcknowledgeEscalation or WorkManagementAction.RecordEscalationAction => "CaseEscalationAcknowledged",
        WorkManagementAction.ResolveEscalation => "CaseEscalationClosed",
        _ => "CaseObservationRecorded"
    };

    private static CaseQueueItem ToQueueItem(FakeCaseState state) => new(
        state.Id,
        state.Workflow,
        "ZW",
        state.OwnerActorId,
        state.Version,
        state.UpdatedAtUtc);

    private static CaseTimeline ToTimeline(FakeCaseState state, IReadOnlyList<CaseTimelineEntry> entries) => new(
        new CaseTimelineHeader(
            state.Id,
            "sample-organization",
            "sample-workspace",
            "ZW",
            state.Workflow,
            state.OwnerActorId,
            state.Version,
            state.ReportRevision,
            state.CreatedAtUtc),
        entries);

    private static WorkManagementItem ToWorkManagementItem(FakeWorkItemState state) => new(
        state.WorkKey,
        state.DisplayReference,
        state.Kind,
        state.Status,
        state.Assignment,
        state.Summary,
        state.UpdatedAtUtc,
        state.DueAtUtc,
        state.AvailableActions.ToArray(),
        state.History.ToArray(),
        state.Kind == WorkManagementKind.Assignment ? Workers : []);

    private static FakeWorkItemState CreateAssistance(Guid id, FakeCaseState state, WorkManagementStatus status) => new(
        id,
        $"sample-assistance-{id:N}",
        "SAMPLE-AID",
        WorkManagementKind.Assistance,
        status,
        WorkManagementAssignmentStatus.AssignedToYou,
            "Assistance request",
        state.UpdatedAtUtc,
        null,
        status is WorkManagementStatus.Requested or WorkManagementStatus.Accepted or WorkManagementStatus.InProgress
            ? [WorkManagementAction.RecordAssistanceProvided, WorkManagementAction.RecordAssistanceUnmet]
            : []);

    private static FakeWorkItemState CreateFollowUp(Guid id, DateTimeOffset dueAtUtc, FakeCaseState state, WorkManagementStatus status) => new(
        id,
        $"sample-follow-up-{id:N}",
        "SAMPLE-FOLLOW-UP",
        WorkManagementKind.FollowUp,
        status,
        WorkManagementAssignmentStatus.AssignedToYou,
        "Follow-up task",
        state.UpdatedAtUtc,
        dueAtUtc,
        status is WorkManagementStatus.Scheduled or WorkManagementStatus.Rescheduled
            ? [WorkManagementAction.RescheduleFollowUp, WorkManagementAction.CancelFollowUp, WorkManagementAction.RecordFollowUpOutcome]
            : [WorkManagementAction.ScheduleFollowUp]);

    private static FakeWorkItemState CreateEscalation(Guid id, FakeCaseState state, WorkManagementStatus status) => new(
        id,
        $"sample-escalation-{id:N}",
        "SAMPLE-ESCALATION",
        WorkManagementKind.Escalation,
        status,
        WorkManagementAssignmentStatus.AssignedToYou,
        "Operational escalation",
        state.UpdatedAtUtc,
        null,
        status switch
        {
            WorkManagementStatus.Pending => [WorkManagementAction.RequestEscalation],
            WorkManagementStatus.Open => [WorkManagementAction.AcknowledgeEscalation, WorkManagementAction.ResolveEscalation],
            WorkManagementStatus.Acknowledged => [WorkManagementAction.RecordEscalationAction, WorkManagementAction.ResolveEscalation],
            _ => []
        });

    private static FakeCaseState CreateCase(
        Guid caseId,
        string owner,
        string workflow,
        DateTimeOffset createdAtUtc)
    {
        var state = new FakeCaseState(caseId, owner, workflow, createdAtUtc);
        state.Timeline.Add(new CaseTimelineEntry(
            Guid.NewGuid(),
            "CaseReported",
            "Report received",
            CaseTimelineEntryKind.Derived,
            state.Version,
            state.ReportRevision,
            Guid.NewGuid(),
            "sample-reporter",
            createdAtUtc,
            createdAtUtc,
            1));

        var assignmentId = Guid.Parse(caseId == CaseOneId
            ? "c7cad8ec-a5d2-4d97-a0ea-0edc3a8a0101"
            : "c7cad8ec-a5d2-4d97-a0ea-0edc3a8a0102");
        var assignment = new FakeWorkItemState(
            assignmentId,
            $"sample-assignment-{assignmentId:N}",
            $"SAMPLE-{(caseId == CaseOneId ? "A01" : "B02")}",
            WorkManagementKind.Assignment,
            caseId == CaseOneId ? WorkManagementStatus.Assigned : WorkManagementStatus.AcknowledgementPending,
            caseId == CaseOneId ? WorkManagementAssignmentStatus.AssignedToYou : WorkManagementAssignmentStatus.AcknowledgementPending,
            "Case assignment",
            createdAtUtc,
            null,
            caseId == CaseOneId
                ? [WorkManagementAction.ReassignWorker, WorkManagementAction.RequestAssistance, WorkManagementAction.RequestEscalation]
                : [WorkManagementAction.AssignWorker, WorkManagementAction.RequestAssistance, WorkManagementAction.RequestEscalation]);
        if (caseId == CaseOneId)
        {
            assignment.AssignedWorkerKey = Workers[0].Key;
        }

        state.WorkItems.Add(assignment);

        if (caseId == CaseOneId)
        {
            var assistance = CreateAssistance(Guid.Parse("d8dbe9fd-b6e3-4ea8-b1fb-1fed4b9b0101"), state, WorkManagementStatus.Accepted);
            assistance.AssistanceNeed = WorkManagementAssistanceNeed.Transport;
            state.WorkItems.Add(assistance);
            var inProgressAssistance = CreateAssistance(Guid.Parse("d8dbe9fd-b6e3-4ea8-b1fb-1fed4b9b0102"), state, WorkManagementStatus.InProgress);
            inProgressAssistance.AssistanceNeed = WorkManagementAssistanceNeed.Supplies;
            state.WorkItems.Add(inProgressAssistance);
            state.WorkItems.Add(CreateFollowUp(
                Guid.Parse("e9ecfa0e-c7f4-4fb9-c20c-2f0e5cac0101"),
                createdAtUtc.AddDays(2),
                state,
                WorkManagementStatus.Scheduled));
            state.WorkItems.Add(CreateEscalation(Guid.Parse("fa0d0b1f-d805-40ca-d31d-301f6dbd0101"), state, WorkManagementStatus.Open));
        }
        else
        {
            var overdueFollowUp = CreateFollowUp(
                Guid.Parse("e9ecfa0e-c7f4-4fb9-c20c-2f0e5cac0102"),
                createdAtUtc.AddDays(-1),
                state,
                WorkManagementStatus.Missed);
            overdueFollowUp.AvailableActions = [WorkManagementAction.RescheduleFollowUp, WorkManagementAction.CancelFollowUp, WorkManagementAction.RecordFollowUpOutcome];
            state.WorkItems.Add(overdueFollowUp);
            var unreachableFollowUp = CreateFollowUp(
                Guid.Parse("e9ecfa0e-c7f4-4fb9-c20c-2f0e5cac0104"),
                createdAtUtc.AddDays(-2),
                state,
                WorkManagementStatus.Unreachable);
            unreachableFollowUp.AvailableActions = [WorkManagementAction.RescheduleFollowUp, WorkManagementAction.CancelFollowUp];
            state.WorkItems.Add(unreachableFollowUp);
            state.WorkItems.Add(new FakeWorkItemState(
                Guid.Parse("e9ecfa0e-c7f4-4fb9-c20c-2f0e5cac0103"),
                "sample-follow-up-schedule",
                "SAMPLE-FOLLOW-UP-NEW",
                WorkManagementKind.FollowUp,
                WorkManagementStatus.Open,
                WorkManagementAssignmentStatus.AssignedToYou,
                "Follow-up option",
                state.UpdatedAtUtc,
                null,
                [WorkManagementAction.ScheduleFollowUp]));
            state.WorkItems.Add(CreateEscalation(Guid.Parse("fa0d0b1f-d805-40ca-d31d-301f6dbd0102"), state, WorkManagementStatus.Pending));
        }

        return state;
    }

    private static Dictionary<Guid, FakeCaseState> CreateCases(DateTimeOffset now) => new()
    {
        [CaseOneId] = CreateCase(CaseOneId, "sample-coordinator", "UnderReview", now.AddDays(-3)),
        [CaseTwoId] = CreateCase(CaseTwoId, "sample-field-worker", "AwaitingFollowUp", now.AddDays(-5))
    };

    private static Dictionary<Guid, FakeCoordinationState> CreateCoordinationReports(DateTimeOffset now)
    {
        var first = new IntakeCoordinationQueueEntry(
            ReceiptOneId,
            IntakeStatus.ReceivedUnverified,
            IntakeRoutingStatus.NeedsRouting,
            null,
            "ZW",
            "sample-harare-region",
            "sample-area-a",
            "sample-country-catalogue-v1",
            IntakeSubjectScope.OtherGroup,
            now.AddHours(-3),
            1,
            null,
            false);
        var second = new IntakeCoordinationQueueEntry(
            ReceiptTwoId,
            IntakeStatus.ReceivedUnverified,
            IntakeRoutingStatus.NoServiceCoverage,
            null,
            "ZW",
            "sample-bulawayo-region",
            null,
            "sample-country-catalogue-v1",
            IntakeSubjectScope.OtherGroup,
            now.AddHours(-1),
            1,
            null,
            false);
        return new Dictionary<Guid, FakeCoordinationState>
        {
            [first.ReceiptId] = new(first, []),
            [second.ReceiptId] = new(second, [])
        };
    }

    private static readonly WorkManagementWorkerOption[] Workers =
    [
        new("sample-worker-01", "Field worker"),
        new("sample-worker-02", "Coordinator")
    ];

    private sealed class FakeCaseState(Guid id, string ownerActorId, string workflow, DateTimeOffset createdAtUtc)
    {
        public Guid Id { get; } = id;
        public string OwnerActorId { get; } = ownerActorId;
        public string Workflow { get; set; } = workflow;
        public long Version { get; set; } = 1;
        public int ReportRevision { get; set; } = 1;
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
        public DateTimeOffset UpdatedAtUtc { get; set; } = createdAtUtc;
        public List<CaseTimelineEntry> Timeline { get; } = [];
        public Dictionary<Guid, FakeActionRequest> ActionRequests { get; } = [];
        public List<FakeWorkItemState> WorkItems { get; } = [];
    }

    private sealed class FakeWorkItemState(
        Guid id,
        string workKey,
        string displayReference,
        WorkManagementKind kind,
        WorkManagementStatus status,
        WorkManagementAssignmentStatus assignment,
        string summary,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? dueAtUtc,
        IReadOnlyList<WorkManagementAction> availableActions)
    {
        public Guid Id { get; } = id;
        public string WorkKey { get; } = workKey;
        public string DisplayReference { get; } = displayReference;
        public WorkManagementKind Kind { get; } = kind;
        public WorkManagementStatus Status { get; set; } = status;
        public WorkManagementAssignmentStatus Assignment { get; set; } = assignment;
        public string Summary { get; } = summary;
        public DateTimeOffset UpdatedAtUtc { get; set; } = updatedAtUtc;
        public DateTimeOffset? DueAtUtc { get; set; } = dueAtUtc;
        public List<WorkManagementAction> AvailableActions { get; set; } = availableActions.ToList();
        public List<WorkManagementHistoryEntry> History { get; } = [];
        public string? AssignedWorkerKey { get; set; }
        public WorkManagementAssistanceNeed? AssistanceNeed { get; set; }
        public bool AssistanceProvided { get; set; }
        public bool AssistanceReceived { get; set; }
        public WorkManagementEscalationReason? EscalationReason { get; set; }
    }

    private sealed class FakeActionRequest(CaseAction action, bool isPending)
    {
        public CaseAction Action { get; } = action;
        public bool IsPending { get; set; } = isPending;
        public CaseActionReviewOutcome? Outcome { get; set; }
    }

    private sealed class FakeCoordinationState(
        IntakeCoordinationQueueEntry entry,
        List<IntakeCoordinationActionRecord> history)
    {
        public IntakeCoordinationQueueEntry Entry { get; set; } = entry;
        public List<IntakeCoordinationActionRecord> History { get; } = history;
    }
}
