using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Client.Presentation;

/// <summary>
/// Work management backed by the authoritative case service: open follow-ups and
/// raised escalations project from the monitoring queue, versions and ownership
/// come from the timeline header, and every action executes as a version-checked
/// case command. Anything without a command mapping stays fail-closed.
/// </summary>
public sealed class CommandBackedWorkManagementClient(
    ICaseCommandClient commands,
    IMonitoringQueueClient monitoring,
    ICaseTimelineClient timeline) : IWorkManagementClient
{
    private const int MonitoringLimit = 200;
    private const string FollowUpPrefix = "fu:";
    private const string EscalationPrefix = "esc:";
    private const string AssistancePrefix = "ast:";

    private readonly ICaseCommandClient _commands = commands ?? throw new ArgumentNullException(nameof(commands));
    private readonly IMonitoringQueueClient _monitoring = monitoring ?? throw new ArgumentNullException(nameof(monitoring));
    private readonly ICaseTimelineClient _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));

    public async Task<UiCapabilityResult<WorkManagementSnapshot>> GetAsync(
        Guid caseId,
        WorkManagementQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty || !query.IsValid)
        {
            return Invalid<WorkManagementSnapshot>("Review the work filters and try again.");
        }

        var timelineResult = await _timeline.GetAsync(caseId, null, cancellationToken).ConfigureAwait(false);
        if (timelineResult.Access != CaseTimelineAccess.Found || timelineResult.Timeline is not { } timeline)
        {
            return Denied<WorkManagementSnapshot>();
        }

        var header = timeline.Header;
        if (header.CaseId != caseId || header.AggregateVersion <= 0)
        {
            return Unavailable<WorkManagementSnapshot>("The case work could not be confirmed.");
        }

        if (query.Kind == WorkManagementKind.Assignment)
        {
            return CreateSnapshot(header, MapAssignment(header), query);
        }

        MonitoringQueueResult assigned;
        MonitoringQueueResult open;
        try
        {
            var assignedTask = _monitoring.GetAssignedAsync(MonitoringLimit, cancellationToken);
            var openTask = _monitoring.GetOpenAsync(MonitoringLimit, cancellationToken);
            await Task.WhenAll(assignedTask, openTask).ConfigureAwait(false);
            assigned = await assignedTask.ConfigureAwait(false);
            open = await openTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable<WorkManagementSnapshot>("Coordination work is temporarily unavailable.");
        }

        if (assigned.Access == CaseQueueAccess.Denied && open.Access == CaseQueueAccess.Denied)
        {
            return Denied<WorkManagementSnapshot>();
        }

        if (assigned.Access != CaseQueueAccess.Allowed && open.Access != CaseQueueAccess.Allowed)
        {
            return Unavailable<WorkManagementSnapshot>("Coordination work is temporarily unavailable.");
        }

        var items = (assigned.Access == CaseQueueAccess.Allowed ? assigned.Items : [])
            .Concat(open.Access == CaseQueueAccess.Allowed ? open.Items : [])
            .Where(item => item.CaseId == caseId)
            .GroupBy(item => (item.Kind, item.WorkItemId))
            .Select(group => group.First())
            .Select(item => TryMapItem(item, assigned.Access == CaseQueueAccess.Allowed && assigned.Items.Any(assignedItem =>
                assignedItem.Kind == item.Kind && assignedItem.WorkItemId == item.WorkItemId)))
            .Where(mapped => mapped is not null)
            .Select(mapped => mapped!)
            .Concat(MapAssignment(header));

        return CreateSnapshot(header, items, query);
    }

    private static UiCapabilityResult<WorkManagementSnapshot> CreateSnapshot(
        CaseTimelineHeader header, IEnumerable<WorkManagementItem> source, WorkManagementQuery query)
    {
        var items = source.Where(item => MatchesQuery(item, query)).ToArray();
        var totalCount = items.Length;
        var pageItems = items
            .Skip((query.Page.PageNumber - 1) * query.Page.PageSize)
            .Take(query.Page.PageSize)
            .ToArray();

        return Success(new WorkManagementSnapshot(
            header.CaseId,
            header.AggregateVersion,
            new Page<WorkManagementItem>(
                pageItems,
                query.Page.PageNumber,
                query.Page.PageSize,
                totalCount,
                query.Page.PageNumber * query.Page.PageSize < totalCount)));
    }

    public async Task<UiCapabilityResult<WorkManagementSnapshot>> ExecuteAsync(
        WorkManagementCommand command,
        WorkManagementQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!command.IsValid || !query.IsValid)
        {
            return Invalid<WorkManagementSnapshot>("Check the action details and try again.");
        }

        var assignee = command.WorkerKey;
        if ((command.Action is WorkManagementAction.ScheduleFollowUp or WorkManagementAction.RequestAssistance) &&
            string.IsNullOrWhiteSpace(assignee))
        {
            assignee = await GetAccountableOwnerAsync(command.CaseId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(assignee))
            {
                return Invalid<WorkManagementSnapshot>(
                    "A new follow-up needs an accountable owner, which is not available for this case.");
            }
        }

        var input = MapCommand(command, assignee);
        if (input is null)
        {
            return Unavailable<WorkManagementSnapshot>(
                "This action is not supported by the case service yet. Refresh coordination work to see available actions.");
        }

        CaseCommandResult result;
        try
        {
            result = await _commands.SendAsync(command.CaseId, input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable<WorkManagementSnapshot>("The action could not reach the case service.");
        }

        return result.Status switch
        {
            CaseCommandStatus.Accepted or CaseCommandStatus.Replayed =>
                await GetAsync(command.CaseId, query, cancellationToken).ConfigureAwait(false),
            CaseCommandStatus.Conflict => Conflict<WorkManagementSnapshot>(
                "Coordination work changed. Refresh and try again."),
            CaseCommandStatus.Denied => Denied<WorkManagementSnapshot>(),
            CaseCommandStatus.Invalid => Invalid<WorkManagementSnapshot>(
                "The case service rejected this action. Refresh coordination work and try again."),
            _ => Unavailable<WorkManagementSnapshot>("The action could not be completed. Try again."),
        };
    }

    private static bool MatchesQuery(WorkManagementItem item, WorkManagementQuery query)
    {
        if (query.Kind is { } kind && item.Kind != kind)
        {
            return false;
        }

        if (query.Status is { } status && item.Status != status)
        {
            return false;
        }

        if (query.OverdueOnly is { } overdueOnly)
        {
            var isOverdue = item.DueAtUtc is { } due && due < DateTimeOffset.UtcNow &&
                item.Status is WorkManagementStatus.Scheduled or WorkManagementStatus.Requested or
                    WorkManagementStatus.Accepted or WorkManagementStatus.InProgress or WorkManagementStatus.Provided;
            if (isOverdue != overdueOnly)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<WorkManagementItem> MapAssignment(CaseTimelineHeader header)
    {
        if (header.AssignmentVersion is not > 0 || header.AssignmentVersion > header.AggregateVersion ||
            header.AssignmentRecordedAtUtc is null)
        {
            yield break;
        }

        var acknowledged = header.AssignmentAcknowledgedAtUtc.HasValue;
        var canAcknowledge = !acknowledged && header.Workflow != "Resolved" && header.CanAcknowledgeAssignment;
        yield return new WorkManagementItem(
            $"assignment:{header.AssignmentVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            "Case assignment", WorkManagementKind.Assignment,
            acknowledged ? WorkManagementStatus.Acknowledged : WorkManagementStatus.AcknowledgementPending,
            acknowledged ? WorkManagementAssignmentStatus.Assigned : WorkManagementAssignmentStatus.AcknowledgementPending,
            acknowledged ? "The accountable owner acknowledged this assignment." : "Waiting for the accountable owner to acknowledge this assignment.",
            header.AssignmentAcknowledgedAtUtc ?? header.AssignmentRecordedAtUtc.Value, null,
            canAcknowledge ? [WorkManagementAction.AcknowledgeAssignment] : [], [], [],
            CanAcknowledgeAssignment: canAcknowledge);
    }

    private static WorkManagementItem? TryMapItem(MonitoringQueueItem item, bool assignedToCurrentActor = false)
    {
        if (item.CaseId == Guid.Empty || item.WorkItemId == Guid.Empty)
        {
            return null;
        }

        return item.Kind switch
        {
            MonitoringQueueKind.FollowUp => MapFollowUp(item, assignedToCurrentActor),
            MonitoringQueueKind.Escalation => MapEscalation(item),
            MonitoringQueueKind.Assistance => MapAssistance(item),
            _ => null,
        };
    }

    private static WorkManagementItem? MapAssistance(MonitoringQueueItem item)
    {
        if (!Enum.TryParse<WorkManagementStatus>(item.Status, ignoreCase: false, out var status) ||
            status is not (WorkManagementStatus.Requested or WorkManagementStatus.Accepted or
                WorkManagementStatus.InProgress or WorkManagementStatus.Provided or WorkManagementStatus.Unfulfilled))
        {
            return null;
        }

        IReadOnlyList<WorkManagementAction> actions = status switch
        {
            WorkManagementStatus.Requested => [WorkManagementAction.AcceptAssistance],
            WorkManagementStatus.Accepted => [WorkManagementAction.RecordAssistanceProgress, WorkManagementAction.RecordAssistanceUnmet, WorkManagementAction.CancelAssistance],
            WorkManagementStatus.InProgress => [WorkManagementAction.RecordAssistanceProvided, WorkManagementAction.RecordAssistanceUnmet, WorkManagementAction.CancelAssistance],
            WorkManagementStatus.Provided => [WorkManagementAction.ConfirmAssistanceReceived],
            _ => []
        };

        return new WorkManagementItem(
            $"{AssistancePrefix}{item.WorkItemId:D}",
            $"AST-{item.WorkItemId:N}"[..12].ToUpperInvariant(),
            WorkManagementKind.Assistance,
            status,
            WorkManagementAssignmentStatus.Assigned,
            string.IsNullOrWhiteSpace(item.Summary) ? "Assistance request" : item.Summary,
            item.RecordedAtUtc,
            item.DueAtUtc,
            actions,
            [],
            [],
            item.AssigneeActorId,
            AssignedWorkerLabel: null,
            CanAcknowledgeAssignment: false);
    }

    private static WorkManagementItem? MapFollowUp(MonitoringQueueItem item, bool assignedToCurrentActor)
    {
        var status = item.Status switch
        {
            "Scheduled" => WorkManagementStatus.Scheduled,
            "Acknowledged" => WorkManagementStatus.Acknowledged,
            "Completed" => WorkManagementStatus.Completed,
            "Missed" => WorkManagementStatus.Missed,
            "Unreachable" => WorkManagementStatus.Unreachable,
            "Cancelled" => WorkManagementStatus.Cancelled,
            _ => (WorkManagementStatus?)null,
        };

        if (status is null)
        {
            return null;
        }

        var actions = status == WorkManagementStatus.Scheduled
            ? (IReadOnlyList<WorkManagementAction>)[
                WorkManagementAction.ScheduleFollowUp,
                WorkManagementAction.RescheduleFollowUp,
                WorkManagementAction.RecordFollowUpOutcome,
                WorkManagementAction.CancelFollowUp]
            : [];
        if (status == WorkManagementStatus.Scheduled && assignedToCurrentActor)
        {
            actions = [.. actions, WorkManagementAction.AcknowledgeFollowUp];
        }
        else if (status == WorkManagementStatus.Acknowledged)
        {
            actions = [WorkManagementAction.RescheduleFollowUp, WorkManagementAction.RecordFollowUpOutcome, WorkManagementAction.CancelFollowUp];
        }

        return new WorkManagementItem(
            $"{FollowUpPrefix}{item.WorkItemId:D}",
            $"FU-{item.WorkItemId:N}"[..11].ToUpperInvariant(),
            WorkManagementKind.FollowUp,
            status.Value,
            string.IsNullOrWhiteSpace(item.AssigneeActorId)
                ? WorkManagementAssignmentStatus.Unassigned
                : WorkManagementAssignmentStatus.Assigned,
            string.IsNullOrWhiteSpace(item.Summary) ? "Follow-up" : item.Summary,
            item.RecordedAtUtc,
            item.DueAtUtc,
            actions,
            [],
            [],
            item.AssigneeActorId,
            AssignedWorkerLabel: null,
            CanAcknowledgeAssignment: false);
    }

    private static WorkManagementItem? MapEscalation(MonitoringQueueItem item)
    {
        var status = item.Status switch
        {
            "Raised" => WorkManagementStatus.Requested,
            "Acknowledged" => WorkManagementStatus.Acknowledged,
            "Closed" => (WorkManagementStatus?)null,
            _ => null,
        };

        if (status is null)
        {
            return null;
        }

        var actions = status == WorkManagementStatus.Requested
            ? (IReadOnlyList<WorkManagementAction>)[WorkManagementAction.AcknowledgeEscalation]
            : [];

        return new WorkManagementItem(
            $"{EscalationPrefix}{item.WorkItemId:D}",
            $"ESC-{item.WorkItemId:N}"[..12].ToUpperInvariant(),
            WorkManagementKind.Escalation,
            status.Value,
            WorkManagementAssignmentStatus.Unassigned,
            string.IsNullOrWhiteSpace(item.Summary) ? "Escalation" : item.Summary,
            item.RecordedAtUtc,
            item.DueAtUtc,
            actions,
            [],
            [],
            AssignedWorkerKey: null,
            AssignedWorkerLabel: null,
            CanAcknowledgeAssignment: false);
    }

    private async Task<string?> GetAccountableOwnerAsync(Guid caseId, CancellationToken cancellationToken)
    {
        try
        {
            var timeline = await _timeline.GetAsync(caseId, null, cancellationToken).ConfigureAwait(false);
            var owner = timeline.Timeline?.Header.AccountableOwnerActorId;
            return string.IsNullOrWhiteSpace(owner) ? null : owner;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static CaseCommandInput? MapCommand(WorkManagementCommand command, string? assignee) => command.Action switch
    {
        WorkManagementAction.RequestAssistance when TryNewAssistanceId(command.WorkKey, out var newAssistanceId) &&
            command.AssistanceNeed is { } assistanceNeed =>
            new CaseCommandInput("RequestAssistance", null, null, null, null, null,
                command.ExpectedVersion, CommandId: command.CommandId ?? newAssistanceId,
                AssistanceId: newAssistanceId,
                AssistanceNeed: $"{assistanceNeed}: {command.Details}", ResponsibleActorId: assignee),
        WorkManagementAction.AcceptAssistance when TryAssistanceId(command.WorkKey, out var acceptId) =>
            MapAssistanceUpdate(command, acceptId, AssistanceStatus.Accepted),
        WorkManagementAction.RecordAssistanceProgress when TryAssistanceId(command.WorkKey, out var progressId) =>
            MapAssistanceUpdate(command, progressId, AssistanceStatus.InProgress),
        WorkManagementAction.RecordAssistanceProvided when TryAssistanceId(command.WorkKey, out var providedId) =>
            MapAssistanceUpdate(command, providedId, AssistanceStatus.Provided),
        WorkManagementAction.RecordAssistanceUnmet when TryAssistanceId(command.WorkKey, out var unmetId) =>
            MapAssistanceUpdate(command, unmetId, AssistanceStatus.Unfulfilled),
        WorkManagementAction.CancelAssistance when TryAssistanceId(command.WorkKey, out var cancelledId) =>
            MapAssistanceUpdate(command, cancelledId, AssistanceStatus.Cancelled),
        WorkManagementAction.ConfirmAssistanceReceived when TryAssistanceId(command.WorkKey, out var receiptId) =>
            MapAssistanceUpdate(command, receiptId, AssistanceStatus.ReceiptConfirmed),
        WorkManagementAction.AcknowledgeAssignment when
            command.WorkKey.StartsWith("assignment:", StringComparison.Ordinal) &&
            long.TryParse(command.WorkKey.AsSpan("assignment:".Length),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var assignmentVersion) &&
            assignmentVersion > 0 =>
            new CaseCommandInput("AcknowledgeAssignment", null, null, null, null, null,
                command.ExpectedVersion, AssignmentVersion: assignmentVersion),
        WorkManagementAction.ScheduleFollowUp when command.DueAtUtc is { } due =>
            new CaseCommandInput(
                "ScheduleFollowUp", null, WorkspaceReason("Scheduled"), null, null, null,
                command.ExpectedVersion, FollowUpId: Guid.NewGuid(),
                Purpose: string.IsNullOrWhiteSpace(command.Details)
                    ? "Follow-up scheduled from the case workspace."
                    : command.Details.Trim(),
                AssigneeActorId: assignee,
                DueAtUtc: due),
        WorkManagementAction.AcknowledgeFollowUp when TryFollowUpId(command.WorkKey, out var acknowledgeFollowUpId) =>
            new CaseCommandInput("AcknowledgeFollowUp", null, null, null, null, null,
                command.ExpectedVersion, FollowUpId: acknowledgeFollowUpId),
        WorkManagementAction.RescheduleFollowUp when TryFollowUpId(command.WorkKey, out var followUpId) &&
            command.DueAtUtc is { } &&
            command.NewDueAtUtc is { } =>
            new CaseCommandInput(
                "RecordFollowUpOutcome", null, WorkspaceReason("Rescheduled"), null, null, "Rescheduled",
                command.ExpectedVersion, FollowUpId: followUpId,
                OutcomeName: nameof(FollowUpOutcome.Rescheduled),
                OutcomeNotes: "Rescheduled from the case workspace.",
                NewDueAtUtc: command.NewDueAtUtc),
        WorkManagementAction.CancelFollowUp when TryFollowUpId(command.WorkKey, out var followUpId) =>
            new CaseCommandInput(
                "RecordFollowUpOutcome", null, command.CancellationReason, null, null, "Cancelled",
                command.ExpectedVersion, FollowUpId: followUpId,
                OutcomeName: nameof(FollowUpOutcome.Cancelled),
                OutcomeNotes: command.CancellationReason),
        WorkManagementAction.RecordFollowUpOutcome when TryFollowUpId(command.WorkKey, out var followUpId) &&
            MapOutcome(command.FollowUpOutcome, command.NewDueAtUtc) is { } outcome =>
            new CaseCommandInput(
                "RecordFollowUpOutcome", null, command.Details, null, null, outcome.Name,
                command.ExpectedVersion, FollowUpId: followUpId,
                OutcomeName: outcome.Name,
                OutcomeNotes: command.Details,
                NewDueAtUtc: outcome.NewDue),
        WorkManagementAction.AcknowledgeEscalation when TryEscalationId(command.WorkKey, out var escalationId) =>
            new CaseCommandInput(
                "AcknowledgeEscalation", null, WorkspaceReason("Acknowledged"), null, null, null,
                command.ExpectedVersion, EscalationId: escalationId),
        WorkManagementAction.RecordEscalationAction when TryEscalationId(command.WorkKey, out var escalationId) =>
            new CaseCommandInput(
                "AcknowledgeEscalation", null, WorkspaceReason("Action recorded"), null, null, null,
                command.ExpectedVersion, EscalationId: escalationId,
                ActionSummary: command.Details),
        WorkManagementAction.ResolveEscalation when TryEscalationId(command.WorkKey, out var escalationId) =>
            new CaseCommandInput(
                "CloseEscalation", null, command.Reason, null, null, null,
                command.ExpectedVersion, EscalationId: escalationId,
                Resolution: command.ResolutionSummary),
        _ => null,
    };

    private static CaseCommandInput MapAssistanceUpdate(
        WorkManagementCommand command, Guid assistanceId, AssistanceStatus status) =>
        new("UpdateAssistance", null, null, null, null, null, command.ExpectedVersion,
            CommandId: command.CommandId,
            AssistanceId: assistanceId,
            AssistanceStatus: status,
            AssistanceNote: command.Details,
            ReceiptMethod: command.ReceiptMethod,
            ReceiptContext: command.ReceiptContext);

    private static (string Name, DateTimeOffset? NewDue)? MapOutcome(
        WorkManagementFollowUpOutcome? outcome,
        DateTimeOffset? newDue) => outcome switch
    {
        WorkManagementFollowUpOutcome.ContactMade => (nameof(FollowUpOutcome.Completed), null),
        WorkManagementFollowUpOutcome.NoContact => (nameof(FollowUpOutcome.Missed), null),
        WorkManagementFollowUpOutcome.NeedsAnotherFollowUp => (nameof(FollowUpOutcome.Rescheduled), newDue),
        _ => null,
    };

    private static bool TryFollowUpId(string workKey, out Guid followUpId)
    {
        followUpId = Guid.Empty;
        return workKey.StartsWith(FollowUpPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(workKey.AsSpan(FollowUpPrefix.Length), out followUpId) &&
            followUpId != Guid.Empty;
    }

    private static bool TryEscalationId(string workKey, out Guid escalationId)
    {
        escalationId = Guid.Empty;
        return workKey.StartsWith(EscalationPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(workKey.AsSpan(EscalationPrefix.Length), out escalationId) &&
            escalationId != Guid.Empty;
    }

    private static bool TryAssistanceId(string workKey, out Guid assistanceId)
    {
        assistanceId = Guid.Empty;
        return workKey.StartsWith(AssistancePrefix, StringComparison.Ordinal) &&
            Guid.TryParse(workKey.AsSpan(AssistancePrefix.Length), out assistanceId) && assistanceId != Guid.Empty;
    }

    private static bool TryNewAssistanceId(string workKey, out Guid assistanceId)
    {
        const string prefix = "assistance:new:";
        assistanceId = Guid.Empty;
        return workKey.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParse(workKey.AsSpan(prefix.Length), out assistanceId) && assistanceId != Guid.Empty;
    }

    private static string WorkspaceReason(string verb) => $"{verb} from the case workspace.";

    private static UiCapabilityResult<T> Success<T>(T value) => new(
        UiCapabilityResultCode.Success,
        value,
        new UiCapabilityMetadata(
            "case-work-management",
            UiCapabilityResults.ContractVersion,
            UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow,
            IsAvailable: true));

    private static UiCapabilityResult<T> Invalid<T>(string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        UiCapabilityResults.UnavailableMetadata("case-work-management", message),
        message);

    private static UiCapabilityResult<T> Denied<T>() => new(
        UiCapabilityResultCode.Denied,
        default,
        UiCapabilityResults.UnavailableMetadata(
            "case-work-management", "This case is not available to the current account."),
        "This case is not available to the current account.");

    private static UiCapabilityResult<T> Unavailable<T>(string message) => new(
        UiCapabilityResultCode.Unavailable,
        default,
        UiCapabilityResults.UnavailableMetadata("case-work-management", message),
        message);

    private static UiCapabilityResult<T> Conflict<T>(string message) => new(
        UiCapabilityResultCode.Conflict,
        default,
        UiCapabilityResults.UnavailableMetadata("case-work-management", message),
        message);
}
