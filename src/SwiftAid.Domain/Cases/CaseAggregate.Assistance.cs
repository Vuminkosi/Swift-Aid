using SwiftAid.Contracts.Commands;

namespace SwiftAid.Domain.Cases;

public sealed record AssistanceRequested(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId,
    string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, Guid AssistanceId,
    string Need, string ResponsibleActorId, DateTimeOffset? DueAtUtc)
    : CaseEvent(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc, RecordedAtUtc);

public abstract record AssistanceActivityRecorded(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId,
    string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, Guid AssistanceId,
    AssistanceStatus Status, string Note, string? ReceiptMethod, string? ReceiptContext)
    : CaseEvent(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc, RecordedAtUtc);

public sealed record AssistanceAccepted(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId,
    string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, Guid AssistanceId, string Note)
    : AssistanceActivityRecorded(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc,
        RecordedAtUtc, AssistanceId, AssistanceStatus.Accepted, Note, null, null);

public sealed record AssistanceProgressRecorded(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId,
    string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, Guid AssistanceId, string Note)
    : AssistanceActivityRecorded(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc,
        RecordedAtUtc, AssistanceId, AssistanceStatus.InProgress, Note, null, null);

public sealed record AssistanceProvided(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId,
    string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, Guid AssistanceId, string Note)
    : AssistanceActivityRecorded(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc,
        RecordedAtUtc, AssistanceId, AssistanceStatus.Provided, Note, null, null);

public sealed record AssistanceReceiptConfirmed(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId,
    string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, Guid AssistanceId, string Note, string ReceiptMethod, string ReceiptContext)
    : AssistanceActivityRecorded(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc,
        RecordedAtUtc, AssistanceId, AssistanceStatus.ReceiptConfirmed, Note, ReceiptMethod, ReceiptContext);

public sealed record AssistanceUnfulfilled(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId,
    string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, Guid AssistanceId, string Note)
    : AssistanceActivityRecorded(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc,
        RecordedAtUtc, AssistanceId, AssistanceStatus.Unfulfilled, Note, null, null);

public sealed record AssistanceCancelled(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId,
    string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, Guid AssistanceId, string Note)
    : AssistanceActivityRecorded(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc,
        RecordedAtUtc, AssistanceId, AssistanceStatus.Cancelled, Note, null, null);

public sealed record AssistanceState(Guid AssistanceId, string Need, string ResponsibleActorId,
    string RequestedByActorId, DateTimeOffset RequestedAtUtc, DateTimeOffset? DueAtUtc, AssistanceStatus Status,
    string? AcceptedByActorId = null, string? ProvidedByActorId = null, string? ReceiptRecordedByActorId = null,
    string? ReceiptMethod = null, string? ReceiptContext = null, string? LastNote = null)
{
    public bool ReceiptOutstanding => Status == AssistanceStatus.Provided;
}

public sealed partial class CaseAggregate
{
    private readonly List<AssistanceState> _assistance = [];
    public IReadOnlyList<AssistanceState> Assistance => _assistance.AsReadOnly();

    private void ApplyActionOwnerChanged(ActionOwnerChanged changed)
    {
        if (changed.ReportRevision != ReportRevision || changed.ActionId == Guid.Empty ||
            !Enum.IsDefined(changed.ActionKind))
            throw new InvalidDataException("The action owner change event is invalid.");
        try
        {
            RequireText(changed.PreviousOwnerId, nameof(changed.PreviousOwnerId));
            RequireText(changed.NewOwnerId, nameof(changed.NewOwnerId));
            RequireText(changed.Reason, nameof(changed.Reason));
            if (StringComparer.Ordinal.Equals(changed.PreviousOwnerId, changed.NewOwnerId))
                throw new InvalidOperationException("An owner change must change the owner.");
            switch (changed.ActionKind)
            {
                case ActionOwnerKind.FollowUp:
                    var followUpIndex = _followUps.FindIndex(item => item.FollowUpId == changed.ActionId);
                    if (followUpIndex < 0 || _followUps[followUpIndex].Status is FollowUpStatus.Completed or FollowUpStatus.Cancelled ||
                        !StringComparer.Ordinal.Equals(_followUps[followUpIndex].AssigneeActorId, changed.PreviousOwnerId))
                        throw new InvalidOperationException("The follow-up owner change does not match current open state.");
                    _followUps[followUpIndex] = _followUps[followUpIndex] with { AssigneeActorId = changed.NewOwnerId };
                    break;
                case ActionOwnerKind.Assistance:
                    var assistanceIndex = _assistance.FindIndex(item => item.AssistanceId == changed.ActionId);
                    if (assistanceIndex < 0 || _assistance[assistanceIndex].Status is AssistanceStatus.ReceiptConfirmed or AssistanceStatus.Unfulfilled or AssistanceStatus.Cancelled ||
                        !StringComparer.Ordinal.Equals(_assistance[assistanceIndex].ResponsibleActorId, changed.PreviousOwnerId))
                        throw new InvalidOperationException("The assistance owner change does not match current open state.");
                    _assistance[assistanceIndex] = _assistance[assistanceIndex] with { ResponsibleActorId = changed.NewOwnerId };
                    break;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The action owner change event is invalid.", exception);
        }
    }

    private IReadOnlyList<CaseEvent> DecideRequestAssistance(RequestAssistanceCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.ManageAssistance);
        ValidateAssistanceRequest(command.AssistanceId, command.Need, command.ResponsibleActorId, command.DueAtUtc);
        return [new AssistanceRequested(CaseId, version, ReportRevision, command.CommandId, actor.ActorId,
            command.OccurredAtUtc, command.RecordedAtUtc, command.AssistanceId, command.Need, command.ResponsibleActorId, command.DueAtUtc)];
    }

    private IReadOnlyList<CaseEvent> DecideUpdateAssistance(UpdateAssistanceCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.ManageAssistance);
        ValidateAssistanceActivity(command.AssistanceId, command.Status, command.Note, command.ReceiptMethod, command.ReceiptContext);
        CaseEvent activity = command.Status switch
        {
            AssistanceStatus.Accepted => new AssistanceAccepted(CaseId, version, ReportRevision, command.CommandId, actor.ActorId,
                command.OccurredAtUtc, command.RecordedAtUtc, command.AssistanceId, command.Note),
            AssistanceStatus.InProgress => new AssistanceProgressRecorded(CaseId, version, ReportRevision, command.CommandId, actor.ActorId,
                command.OccurredAtUtc, command.RecordedAtUtc, command.AssistanceId, command.Note),
            AssistanceStatus.Provided => new AssistanceProvided(CaseId, version, ReportRevision, command.CommandId, actor.ActorId,
                command.OccurredAtUtc, command.RecordedAtUtc, command.AssistanceId, command.Note),
            AssistanceStatus.ReceiptConfirmed => new AssistanceReceiptConfirmed(CaseId, version, ReportRevision, command.CommandId, actor.ActorId,
                command.OccurredAtUtc, command.RecordedAtUtc, command.AssistanceId, command.Note, command.ReceiptMethod!, command.ReceiptContext!),
            AssistanceStatus.Unfulfilled => new AssistanceUnfulfilled(CaseId, version, ReportRevision, command.CommandId, actor.ActorId,
                command.OccurredAtUtc, command.RecordedAtUtc, command.AssistanceId, command.Note),
            AssistanceStatus.Cancelled => new AssistanceCancelled(CaseId, version, ReportRevision, command.CommandId, actor.ActorId,
                command.OccurredAtUtc, command.RecordedAtUtc, command.AssistanceId, command.Note),
            _ => throw new InvalidOperationException("Unsupported assistance activity.")
        };
        return [activity];
    }

    private void ValidateAssistanceRequest(Guid id, string need, string owner, DateTimeOffset? due)
    {
        RequireUnresolvedMonitoringTarget("requesting assistance");
        if (id == Guid.Empty || _assistance.Any(item => item.AssistanceId == id))
            throw new ArgumentException("A new assistance identifier is required.");
        RequireText(need, nameof(need));
        RequireText(owner, nameof(owner));
        if (due is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new ArgumentException("The selected due time must use UTC.");
    }

    private int ValidateAssistanceActivity(Guid id, AssistanceStatus status, string note, string? method, string? context)
    {
        RequireUnresolvedMonitoringTarget("recording assistance activity");
        var index = _assistance.FindIndex(item => item.AssistanceId == id);
        if (index < 0) throw new InvalidOperationException("The assistance request does not exist.");
        RequireText(note, nameof(note));
        var current = _assistance[index].Status;
        var allowed = (current, status) switch
        {
            (AssistanceStatus.Requested, AssistanceStatus.Accepted) => true,
            (AssistanceStatus.Accepted, AssistanceStatus.InProgress) => true,
            (AssistanceStatus.InProgress, AssistanceStatus.Provided) => true,
            (AssistanceStatus.Provided, AssistanceStatus.ReceiptConfirmed) => true,
            (AssistanceStatus.Requested or AssistanceStatus.Accepted or AssistanceStatus.InProgress,
                AssistanceStatus.Unfulfilled or AssistanceStatus.Cancelled) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("The assistance transition is not permitted.");
        if (status == AssistanceStatus.ReceiptConfirmed)
        {
            RequireText(method!, nameof(method));
            RequireText(context!, nameof(context));
        }
        else if (method is not null || context is not null)
            throw new ArgumentException("Receipt provenance belongs only to receipt confirmation.");
        return index;
    }

    private void ApplyAssistanceRequest(AssistanceRequested requested)
    {
        if (requested.ReportRevision != ReportRevision) throw new InvalidDataException("Assistance has a stale report revision.");
        try
        {
            ValidateAssistanceRequest(requested.AssistanceId, requested.Need, requested.ResponsibleActorId, requested.DueAtUtc);
            _assistance.Add(new(requested.AssistanceId, requested.Need, requested.ResponsibleActorId,
                requested.ActorId, requested.RecordedAtUtc, requested.DueAtUtc, AssistanceStatus.Requested));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The assistance request event is invalid.", exception);
        }
    }

    private void ApplyAssistanceActivity(AssistanceActivityRecorded activity)
    {
        if (activity.ReportRevision != ReportRevision) throw new InvalidDataException("Assistance has a stale report revision.");
        var expected = activity switch
        {
            AssistanceAccepted => AssistanceStatus.Accepted,
            AssistanceProgressRecorded => AssistanceStatus.InProgress,
            AssistanceProvided => AssistanceStatus.Provided,
            AssistanceReceiptConfirmed => AssistanceStatus.ReceiptConfirmed,
            AssistanceUnfulfilled => AssistanceStatus.Unfulfilled,
            AssistanceCancelled => AssistanceStatus.Cancelled,
            _ => throw new InvalidDataException("Unknown assistance activity event.")
        };
        if (activity.Status != expected) throw new InvalidDataException("Assistance event type and status do not match.");
        try
        {
            var index = ValidateAssistanceActivity(activity.AssistanceId, activity.Status, activity.Note, activity.ReceiptMethod, activity.ReceiptContext);
            var current = _assistance[index];
            _assistance[index] = current with
            {
                Status = activity.Status,
                LastNote = activity.Note,
                AcceptedByActorId = activity.Status == AssistanceStatus.Accepted ? activity.ActorId : current.AcceptedByActorId,
                ProvidedByActorId = activity.Status == AssistanceStatus.Provided ? activity.ActorId : current.ProvidedByActorId,
                ReceiptRecordedByActorId = activity.Status == AssistanceStatus.ReceiptConfirmed ? activity.ActorId : current.ReceiptRecordedByActorId,
                ReceiptMethod = activity.ReceiptMethod,
                ReceiptContext = activity.ReceiptContext
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The assistance activity event is invalid.", exception);
        }
    }
}
