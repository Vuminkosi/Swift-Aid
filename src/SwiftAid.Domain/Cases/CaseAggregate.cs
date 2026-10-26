using System.Buffers.Binary;
using System.Security.Cryptography;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Domain.Cases;

public enum CaseWorkflow { Reported, UnderReview, Active, AwaitingFollowUp, Resolved }
public enum ClinicalClassification { Unclassified, Suspected, Probable, Confirmed, RuledOut }
public enum LaboratoryState { NotRecorded, Requested, Collected, Pending, ResultRecorded }
public enum ReportedValueState { Unknown, NotRecorded, NotApplicable, Absent, Known }

/// <summary>Preserves missingness semantics instead of collapsing them into nullable values or zero.</summary>
public readonly record struct ReportedValue<T>
{
    private readonly T? _value;

    private ReportedValue(ReportedValueState state, T? value)
    {
        State = state;
        _value = value;
    }

    public ReportedValueState State { get; }

    public bool HasValue => State == ReportedValueState.Known;

    public T Value => HasValue ? _value! : throw new InvalidOperationException("A value is available only when its presence is Known.");

    public static ReportedValue<T> Unknown => new(ReportedValueState.Unknown, default);
    public static ReportedValue<T> NotRecorded => new(ReportedValueState.NotRecorded, default);
    public static ReportedValue<T> NotApplicable => new(ReportedValueState.NotApplicable, default);
    public static ReportedValue<T> Absent => new(ReportedValueState.Absent, default);
    public static ReportedValue<T> Known(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ReportedValue<T>(ReportedValueState.Known, value);
    }
}

public abstract record CaseEvent(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId, string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc)
{
    public Guid EventId => DeterministicCaseEventId.Create(CaseId, CommandId, AggregateVersion);
}

internal static class DeterministicCaseEventId
{
    public static Guid Create(Guid caseId, Guid commandId, long aggregateVersion)
    {
        Span<byte> input = stackalloc byte[40];
        caseId.TryWriteBytes(input);
        commandId.TryWriteBytes(input[16..]);
        BinaryPrimitives.WriteInt64BigEndian(input[32..], aggregateVersion);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }
}
public sealed record CaseReported(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId, string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, ReportFacts Report, string WorkspaceId) : CaseEvent(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc, RecordedAtUtc);
public sealed record CaseAssigned(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId, string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, string AccountableOwnerId) : CaseEvent(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc, RecordedAtUtc);
public sealed record CaseReportCorrected(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId, string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, ReportFacts CorrectedReport, string Reason, long PreviousReportRevision) : CaseEvent(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc, RecordedAtUtc);
public sealed record CaseActionRequested(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId, string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, Guid RequestId, CaseAction RequestedOperation, string Reason, IReadOnlyList<string> EvidenceEventIds, string? ProposedOwnerId) : CaseEvent(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc, RecordedAtUtc);
public sealed record CaseActionRequestReviewed(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId, string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, Guid RequestId, CaseActionReviewOutcome Outcome, Guid? LinkedAcceptedEventId, string Reason) : CaseEvent(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc, RecordedAtUtc);
public sealed record CaseResolved(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId, string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, string Reason) : CaseEvent(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc, RecordedAtUtc);
public sealed record CaseReopened(Guid CaseId, long AggregateVersion, long ReportRevision, Guid CommandId, string ActorId, DateTimeOffset? OccurredAtUtc, DateTimeOffset RecordedAtUtc, string Reason, string AccountableOwnerId) : CaseEvent(CaseId, AggregateVersion, ReportRevision, CommandId, ActorId, OccurredAtUtc, RecordedAtUtc);

/// <summary>Immutable case state rebuilt from its ordered event stream.</summary>
public sealed class CaseAggregate
{
    private readonly List<CaseEvent> _events = [];
    private CaseAssigned? _pendingAssignment;

    private CaseAggregate() => Classification = ClinicalClassification.Unclassified;

    public Guid CaseId { get; private set; }
    public long AggregateVersion { get; private set; }
    public long ReportRevision { get; private set; }
    public long WorkflowVersion { get; private set; }
    public long ClassificationVersion { get; private set; }
    public long LaboratoryVersion { get; private set; }
    public long OutcomeVersion { get; private set; }
    public ReportFacts? Report { get; private set; }
    public string? WorkspaceId { get; private set; }
    public string? AccountableOwnerId { get; private set; }
    public CaseWorkflow Workflow { get; private set; } = CaseWorkflow.Reported;
    public ClinicalClassification Classification { get; private set; }
    public LaboratoryState Laboratory { get; private set; } = LaboratoryState.NotRecorded;
    public ReportedValue<string> Outcome { get; private set; } = ReportedValue<string>.Unknown;
    public IReadOnlyList<CaseEvent> Events => Array.AsReadOnly(_events.Select(SnapshotEvent).ToArray());

    public static IReadOnlyList<CaseEvent> Create(AcceptCaseCommand command, ActorCapabilities actor)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        if (command.CaseId == Guid.Empty) throw new ArgumentException("A case identifier is required.", nameof(command));
        if (command.CommandId == Guid.Empty) throw new ArgumentException("A command identifier is required.", nameof(command.CommandId));
        ValidateRecordedAtUtc(command.RecordedAtUtc);
        RequireCapability(actor, CaseCapability.AcceptReport);
        RequireCapability(actor, CaseCapability.AssignCase);
        ValidateReport(command.Report);
        RequireText(command.AccountableOwnerId, nameof(command.AccountableOwnerId));
        RequireText(command.WorkspaceId, nameof(command.WorkspaceId));
        return Array.AsReadOnly<CaseEvent>(
        [
            new CaseReported(command.CaseId, 1, 1, command.CommandId, actor.ActorId, command.OccurredAtUtc, command.RecordedAtUtc, SnapshotReport(command.Report), command.WorkspaceId),
            new CaseAssigned(command.CaseId, 2, 1, command.CommandId, actor.ActorId, command.OccurredAtUtc, command.RecordedAtUtc, command.AccountableOwnerId)
        ]);
    }

    public static CaseAggregate Replay(IEnumerable<CaseEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var aggregate = new CaseAggregate();
        foreach (var @event in events) aggregate.Apply(@event);
        if (aggregate._pendingAssignment is not null) throw new InvalidDataException("A reassignment must be immediately followed by its approved request-review event.");
        if (aggregate.AggregateVersion < 2) throw new InvalidDataException("A managed case must have an initial accountable assignment.");
        return aggregate;
    }

    public IReadOnlyList<CaseEvent> Decide(CaseCommand command, ActorCapabilities actor)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        if (_pendingAssignment is not null) throw new InvalidOperationException("A reassignment is awaiting its approved review event.");
        if (AggregateVersion < 2 || Report is null) throw new InvalidOperationException("The case must be accepted before it can be changed.");
        if (command.CommandId == Guid.Empty) throw new ArgumentException("A command identifier is required.", nameof(command.CommandId));
        ValidateRecordedAtUtc(command.RecordedAtUtc);
        if (command.ExpectedVersion != AggregateVersion) throw new InvalidOperationException("The command version is stale; refresh before changing this case.");
        var version = AggregateVersion + 1;
        return command switch
        {
            CorrectReportCommand correction => DecideCorrection(correction, actor, version),
            RequestCaseActionCommand request => DecideRequest(request, actor, version),
            ReviewCaseActionRequestCommand review => DecideReview(review, actor, version),
            ResolveCaseCommand resolution => DecideResolution(resolution, actor, version),
            ReopenCaseCommand reopening => DecideReopen(reopening, actor, version),
            _ => throw new NotSupportedException($"The command type {command.GetType().Name} is not supported by this aggregate.")
        };
    }

    public void Apply(CaseEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        @event = SnapshotEvent(@event);
        ValidateRecordedAtUtc(@event.RecordedAtUtc);
        var expectedVersion = AggregateVersion + (_pendingAssignment is null ? 1 : 2);
        if (@event.AggregateVersion != expectedVersion) throw new InvalidDataException("Case events must use consecutive aggregate versions.");
        if (string.IsNullOrWhiteSpace(@event.ActorId) || @event.CommandId == Guid.Empty) throw new InvalidDataException("Case events require a trusted actor and command identifier.");

        if (_pendingAssignment is not null)
        {
            if (@event is not CaseActionRequestReviewed review) throw new InvalidDataException("A reassignment must be immediately followed by its approved request-review event.");
            ValidateApprovedAssignmentPair(_pendingAssignment, review);
            ApplyPairedAssignment(_pendingAssignment);
            _pendingAssignment = null;
            ApplyCore(review);
            return;
        }

        if (AggregateVersion >= 2 && @event is CaseAssigned assignment)
        {
            ValidateAssignmentCandidate(assignment);
            _pendingAssignment = assignment;
            return;
        }

        ApplyCore(@event);
    }

    private void ApplyCore(CaseEvent @event)
    {
        ValidateRecordedAtUtc(@event.RecordedAtUtc);
        if (@event.AggregateVersion != AggregateVersion + 1) throw new InvalidDataException("Case events must use consecutive aggregate versions.");
        if (string.IsNullOrWhiteSpace(@event.ActorId) || @event.CommandId == Guid.Empty) throw new InvalidDataException("Case events require a trusted actor and command identifier.");
        if (AggregateVersion == 0)
        {
            if (@event is not CaseReported first || first.AggregateVersion != 1 || first.ReportRevision != 1) throw new InvalidDataException("The first event must be CaseReported at report revision one.");
            ValidateReport(first.Report);
            RequireText(first.WorkspaceId, nameof(first.WorkspaceId));
            CaseId = first.CaseId;
            ReportRevision = first.ReportRevision;
            WorkflowVersion = first.AggregateVersion;
            Report = SnapshotReport(first.Report);
            WorkspaceId = first.WorkspaceId;
        }
        else
        {
            if (@event.CaseId != CaseId) throw new InvalidDataException("An event from another case cannot be applied to this aggregate.");
            if (AggregateVersion == 1 && @event is not CaseAssigned) throw new InvalidDataException("Initial case acceptance must append its accountable assignment as the second event.");
            ApplyExistingEvent(@event);
        }
        AggregateVersion = @event.AggregateVersion;
        _events.Add(@event);
    }

    private void ValidateAssignmentCandidate(CaseAssigned assignment)
    {
        if (assignment.CaseId != CaseId || assignment.ReportRevision != ReportRevision || Workflow == CaseWorkflow.Resolved)
        {
            throw new InvalidDataException("A reassignment must reference the current unresolved case.");
        }

        RequireText(assignment.AccountableOwnerId, nameof(assignment.AccountableOwnerId));
        var hasPendingReassignment = _events.OfType<CaseActionRequested>().Any(request =>
            request.RequestedOperation == CaseAction.Reassign &&
            !_events.OfType<CaseActionRequestReviewed>().Any(review => review.RequestId == request.RequestId && review.Outcome != CaseActionReviewOutcome.Pending));
        if (!hasPendingReassignment) throw new InvalidDataException("A reassignment must have a pending protected-action request.");
    }

    private void ValidateApprovedAssignmentPair(CaseAssigned assignment, CaseActionRequestReviewed review)
    {
        RequireText(review.Reason, nameof(review.Reason));
        if (review.Outcome != CaseActionReviewOutcome.Approved ||
            review.LinkedAcceptedEventId != assignment.EventId ||
            review.CommandId != assignment.CommandId ||
            review.AggregateVersion != assignment.AggregateVersion + 1 ||
            review.CaseId != assignment.CaseId ||
            review.ReportRevision != ReportRevision)
        {
            throw new InvalidDataException("A reassignment must be linked to its immediately following approved review.");
        }

        var request = _events.OfType<CaseActionRequested>().SingleOrDefault(item => item.RequestId == review.RequestId);
        if (request is null || request.RequestedOperation != CaseAction.Reassign)
        {
            throw new InvalidDataException("The approved review must refer to a matching reassignment request.");
        }

        if (_events.OfType<CaseActionRequestReviewed>().Any(item => item.RequestId == request.RequestId && item.Outcome != CaseActionReviewOutcome.Pending))
        {
            throw new InvalidDataException("A completed request decision cannot be changed.");
        }
    }

    private void ApplyPairedAssignment(CaseAssigned assignment)
    {
        if (assignment.AggregateVersion != AggregateVersion + 1 || assignment.CaseId != CaseId || assignment.ReportRevision != ReportRevision || Workflow == CaseWorkflow.Resolved)
        {
            throw new InvalidDataException("A reassignment must reference the current unresolved case.");
        }

        AccountableOwnerId = assignment.AccountableOwnerId;
        AggregateVersion = assignment.AggregateVersion;
        _events.Add(assignment);
    }

    private IReadOnlyList<CaseEvent> DecideCorrection(CorrectReportCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.CorrectReport);
        RequireText(command.Reason, nameof(command.Reason));
        RequireText(command.CorrectedNarrative, nameof(command.CorrectedNarrative));
        var corrected = Report! with { Narrative = command.CorrectedNarrative };
        return Array.AsReadOnly<CaseEvent>([new CaseReportCorrected(CaseId, version, ReportRevision + 1, command.CommandId, actor.ActorId, command.OccurredAtUtc, command.RecordedAtUtc, corrected, command.Reason, ReportRevision)]);
    }

    private IReadOnlyList<CaseEvent> DecideRequest(RequestCaseActionCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.RequestProtectedAction);
        RequireText(command.Reason, nameof(command.Reason));
        if (!Enum.IsDefined(command.Action)) throw new ArgumentOutOfRangeException(nameof(command.Action));
        if (command.Action == CaseAction.Reassign) RequireText(command.ProposedOwnerId, nameof(command.ProposedOwnerId));
        if (command.RequestId == Guid.Empty) throw new ArgumentException("An action request identifier is required.", nameof(command.RequestId));
        ArgumentNullException.ThrowIfNull(command.EvidenceEventIds);
        return Array.AsReadOnly<CaseEvent>([new CaseActionRequested(CaseId, version, ReportRevision, command.CommandId, actor.ActorId, command.OccurredAtUtc, command.RecordedAtUtc,
            command.RequestId, command.Action, command.Reason, Array.AsReadOnly(command.EvidenceEventIds.ToArray()), command.ProposedOwnerId)]);
    }

    private IReadOnlyList<CaseEvent> DecideReview(ReviewCaseActionRequestCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.ReviewProtectedAction);
        RequireText(command.Reason, nameof(command.Reason));
        if (!Enum.IsDefined(command.Outcome)) throw new ArgumentOutOfRangeException(nameof(command.Outcome));
        var request = _events.OfType<CaseActionRequested>().SingleOrDefault(item => item.RequestId == command.RequestId)
            ?? throw new InvalidOperationException("The requested protected action does not exist on this case.");
        if (_events.OfType<CaseActionRequestReviewed>().Any(item => item.RequestId == command.RequestId && item.Outcome != CaseActionReviewOutcome.Pending))
        {
            throw new InvalidOperationException("A completed request decision cannot be changed.");
        }

        if (command.Outcome != CaseActionReviewOutcome.Approved)
        {
            return Array.AsReadOnly<CaseEvent>([new CaseActionRequestReviewed(CaseId, version, ReportRevision, command.CommandId, actor.ActorId,
                command.OccurredAtUtc, command.RecordedAtUtc, command.RequestId, command.Outcome, null, command.Reason)]);
        }

        var acceptedVersion = version;
        CaseEvent acceptedEvent = request.RequestedOperation switch
        {
            CaseAction.Reassign => CreateAcceptedAssignment(request, command, actor, acceptedVersion),
            CaseAction.Resolve => CreateAcceptedResolution(request, command, actor, acceptedVersion),
            CaseAction.Reopen => CreateAcceptedReopening(request, command, actor, acceptedVersion),
            _ => throw new InvalidDataException("The request contains an unknown protected operation.")
        };
        var review = new CaseActionRequestReviewed(CaseId, acceptedVersion + 1, ReportRevision, command.CommandId, actor.ActorId,
            command.OccurredAtUtc, command.RecordedAtUtc, command.RequestId, command.Outcome, acceptedEvent.EventId, command.Reason);
        return Array.AsReadOnly([acceptedEvent, review]);
    }

    private CaseEvent CreateAcceptedAssignment(CaseActionRequested request, ReviewCaseActionRequestCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.AssignCase);
        if (Workflow == CaseWorkflow.Resolved) throw new InvalidOperationException("A resolved case must be reopened before reassignment.");
        var ownerId = command.AcceptedOwnerId ?? request.ProposedOwnerId;
        RequireText(ownerId, nameof(command.AcceptedOwnerId));
        return new CaseAssigned(CaseId, version, ReportRevision, command.CommandId, actor.ActorId, command.OccurredAtUtc, command.RecordedAtUtc, ownerId!);
    }

    private CaseEvent CreateAcceptedResolution(CaseActionRequested request, ReviewCaseActionRequestCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.ResolveCase);
        if (Workflow == CaseWorkflow.Resolved) throw new InvalidOperationException("The case is already resolved.");
        return new CaseResolved(CaseId, version, ReportRevision, command.CommandId, actor.ActorId, command.OccurredAtUtc, command.RecordedAtUtc, command.Reason);
    }

    private CaseEvent CreateAcceptedReopening(CaseActionRequested request, ReviewCaseActionRequestCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.ReopenCase);
        if (Workflow != CaseWorkflow.Resolved) throw new InvalidOperationException("Only a resolved case can be reopened.");
        var ownerId = command.AcceptedOwnerId ?? request.ProposedOwnerId;
        RequireText(ownerId, nameof(command.AcceptedOwnerId));
        return new CaseReopened(CaseId, version, ReportRevision, command.CommandId, actor.ActorId, command.OccurredAtUtc, command.RecordedAtUtc, command.Reason, ownerId!);
    }

    private IReadOnlyList<CaseEvent> DecideResolution(ResolveCaseCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.ResolveCase);
        RequireText(command.Reason, nameof(command.Reason));
        if (Workflow == CaseWorkflow.Resolved) throw new InvalidOperationException("The case is already resolved.");
        return Array.AsReadOnly<CaseEvent>([new CaseResolved(CaseId, version, ReportRevision, command.CommandId, actor.ActorId, command.OccurredAtUtc, command.RecordedAtUtc, command.Reason)]);
    }

    private IReadOnlyList<CaseEvent> DecideReopen(ReopenCaseCommand command, ActorCapabilities actor, long version)
    {
        RequireCapability(actor, CaseCapability.ReopenCase);
        RequireText(command.Reason, nameof(command.Reason));
        RequireText(command.AccountableOwnerId, nameof(command.AccountableOwnerId));
        if (Workflow != CaseWorkflow.Resolved) throw new InvalidOperationException("Only a resolved case can be reopened.");
        return Array.AsReadOnly<CaseEvent>([new CaseReopened(CaseId, version, ReportRevision, command.CommandId, actor.ActorId, command.OccurredAtUtc, command.RecordedAtUtc, command.Reason, command.AccountableOwnerId)]);
    }

    private void ApplyExistingEvent(CaseEvent @event)
    {
        switch (@event)
        {
            case CaseAssigned assignment when AggregateVersion == 1 && assignment.ReportRevision == ReportRevision && assignment.CommandId == _events[0].CommandId && assignment.ActorId == _events[0].ActorId:
                RequireText(assignment.AccountableOwnerId, nameof(assignment.AccountableOwnerId));
                AccountableOwnerId = assignment.AccountableOwnerId;
                break;
            case CaseAssigned when AggregateVersion >= 2:
                throw new InvalidDataException("A post-acceptance assignment must be applied together with its approved request-review event.");
            case CaseAssigned:
                throw new InvalidDataException("The initial assignment must immediately follow the report under the same trusted command and actor.");
            case CaseReportCorrected corrected when corrected.PreviousReportRevision == ReportRevision && corrected.ReportRevision == ReportRevision + 1:
                RequireText(corrected.Reason, nameof(corrected.Reason));
                ValidateReport(corrected.CorrectedReport);
                Report = SnapshotReport(corrected.CorrectedReport);
                ReportRevision = corrected.ReportRevision;
                break;
            case CaseReportCorrected:
                throw new InvalidDataException("A correction must advance only the report revision from its current value.");
            case CaseActionRequested request when request.ReportRevision == ReportRevision:
                RequireText(request.Reason, nameof(request.Reason));
                if (request.RequestId == Guid.Empty || _events.OfType<CaseActionRequested>().Any(item => item.RequestId == request.RequestId))
                {
                    throw new InvalidDataException("A protected-action request must have a unique identifier.");
                }

                if (request.EvidenceEventIds.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Action-request evidence references cannot be empty.");
                break;
            case CaseActionRequested:
                throw new InvalidDataException("An action request must reference the current report revision.");
            case CaseResolved resolution when resolution.ReportRevision == ReportRevision && Workflow != CaseWorkflow.Resolved:
                RequireText(resolution.Reason, nameof(resolution.Reason));
                Workflow = CaseWorkflow.Resolved;
                WorkflowVersion = resolution.AggregateVersion;
                break;
            case CaseResolved:
                throw new InvalidDataException("Resolution must refer to the current report and an unresolved case.");
            case CaseActionRequestReviewed review when review.ReportRevision == ReportRevision:
                ApplyReview(review);
                break;
            case CaseActionRequestReviewed:
                throw new InvalidDataException("A request review must reference the current report revision.");
            case CaseReopened reopening when reopening.ReportRevision == ReportRevision && Workflow == CaseWorkflow.Resolved:
                RequireText(reopening.Reason, nameof(reopening.Reason));
                RequireText(reopening.AccountableOwnerId, nameof(reopening.AccountableOwnerId));
                AccountableOwnerId = reopening.AccountableOwnerId;
                Workflow = CaseWorkflow.Active;
                WorkflowVersion = reopening.AggregateVersion;
                break;
            case CaseReopened:
                throw new InvalidDataException("Reopening requires a resolved case and its current report revision.");
            default:
                throw new InvalidDataException($"Event type {@event.GetType().Name} is unsupported by this aggregate version.");
        }
    }

    private void ApplyReview(CaseActionRequestReviewed review)
    {
        RequireText(review.Reason, nameof(review.Reason));
        var requestExists = _events.OfType<CaseActionRequested>().Any(item => item.RequestId == review.RequestId);
        if (!requestExists) throw new InvalidDataException("A review must reference an existing request.");
        var completed = _events.OfType<CaseActionRequestReviewed>().Any(item => item.RequestId == review.RequestId && item.Outcome != CaseActionReviewOutcome.Pending);
        if (completed) throw new InvalidDataException("A completed request decision cannot be changed.");
        if (review.Outcome == CaseActionReviewOutcome.Approved)
        {
            if (review.LinkedAcceptedEventId is null || !_events.Any(item => item.EventId == review.LinkedAcceptedEventId))
            {
                throw new InvalidDataException("Approval must link to an accepted operation event in the same stream.");
            }

            var linkedEvent = _events.Single(item => item.EventId == review.LinkedAcceptedEventId);
            if (linkedEvent.CommandId != review.CommandId || linkedEvent.AggregateVersion != review.AggregateVersion - 1)
            {
                throw new InvalidDataException("Approval must be committed with the operation it authorizes.");
            }

            var request = _events.OfType<CaseActionRequested>().Single(item => item.RequestId == review.RequestId);
            var matchesOperation = request.RequestedOperation switch
            {
                CaseAction.Reassign => linkedEvent is CaseAssigned,
                CaseAction.Resolve => linkedEvent is CaseResolved,
                CaseAction.Reopen => linkedEvent is CaseReopened,
                _ => false
            };
            if (!matchesOperation) throw new InvalidDataException("The linked accepted event does not match the requested operation.");
        }
        else if (review.LinkedAcceptedEventId is not null)
        {
            throw new InvalidDataException("A pending or denied request cannot link an accepted operation.");
        }
    }

    private static void ValidateReport(ReportFacts report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if ((report.Kind == CaseKind.Human && report.Sector != Sector.HumanHealth) || (report.Kind == CaseKind.Animal && report.Sector != Sector.AnimalHealth)) throw new ArgumentException("Case kind and sector must be consistent.", nameof(report));
        ArgumentNullException.ThrowIfNull(report.Observations);
        if (string.IsNullOrWhiteSpace(report.Narrative) && report.Observations.Count == 0) throw new ArgumentException("A report requires an observation or narrative evidence.", nameof(report));
        if (report.Observations.Any(observation => observation is null)) throw new ArgumentException("A report cannot contain a null observation.", nameof(report));
        if (report.Observations.Any(observation => string.IsNullOrWhiteSpace(observation.Narrative))) throw new ArgumentException("Reported observations must preserve non-empty source text.", nameof(report));
    }

    private static ReportFacts SnapshotReport(ReportFacts report) => report with
    {
        Observations = Array.AsReadOnly(report.Observations.ToArray())
    };

    private static CaseEvent SnapshotEvent(CaseEvent @event) => @event switch
    {
        CaseReported reported => reported with { Report = SnapshotReport(reported.Report) },
        CaseAssigned assignment => assignment,
        CaseReportCorrected correction => correction with { CorrectedReport = SnapshotReport(correction.CorrectedReport) },
        CaseActionRequested request => request with { EvidenceEventIds = Array.AsReadOnly(request.EvidenceEventIds.ToArray()) },
        CaseActionRequestReviewed review => review,
        CaseResolved resolution => resolution,
        CaseReopened reopening => reopening,
        _ => throw new InvalidDataException($"Event type {@event.GetType().Name} is unsupported by this aggregate version.")
    };

    private static void RequireCapability(ActorCapabilities actor, CaseCapability capability)
    {
        RequireText(actor.ActorId, nameof(actor.ActorId));
        if (!actor.Has(capability)) throw new UnauthorizedAccessException($"The actor lacks the {capability} capability.");
    }

    private static void RequireText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty value is required.", parameterName);
    }

    private static void ValidateRecordedAtUtc(DateTimeOffset recordedAtUtc)
    {
        if (recordedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("The server-recorded timestamp must use UTC.", nameof(recordedAtUtc));
    }

}
