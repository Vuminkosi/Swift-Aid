using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Events;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Serialization;

/// <summary>Maps domain events to the versioned wire contract and serializes stable JSON.</summary>
public sealed class CaseEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public byte[] SerializeToUtf8Bytes(CaseEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return @event switch
        {
            AssistanceRequested requested => Serialize(requested, CaseEventType.AssistanceRequested,
                new AssistanceRequestedPayload(requested.AssistanceId, requested.Need, requested.ResponsibleActorId, requested.DueAtUtc)),
            AssistanceActivityRecorded activity => SerializeAssistanceActivity(activity),
            CaseReported reported => SerializeReported(reported),
            CaseAssigned assignment => Serialize(assignment, CaseEventType.CaseAssigned,
                new CaseAssignedPayload(assignment.AccountableOwnerId)),
            CaseCollaboratorGranted grant => Serialize(grant, CaseEventType.CaseCollaboratorGranted,
                new CaseCollaboratorGrantedPayload(grant.GrantId, grant.CollaboratorActorId, grant.Actions, grant.ExpiresAtUtc, grant.Reason)),
            CaseCollaboratorRevoked revoke => Serialize(revoke, CaseEventType.CaseCollaboratorRevoked,
                new CaseCollaboratorRevokedPayload(revoke.GrantId, revoke.Reason)),
            CaseAssignmentAcknowledged acknowledgement when acknowledgement.AssignmentVersion > 0 && acknowledgement.AssignmentVersion < acknowledgement.AggregateVersion =>
                Serialize(acknowledgement, CaseEventType.CaseAssignmentAcknowledged,
                    new CaseAssignmentAcknowledgedPayload(acknowledgement.AssignmentVersion)),
            CaseReportCorrected correction => SerializeCorrection(correction),
            CaseActionRequested request => Serialize(request, CaseEventType.CaseActionRequested,
                MapRequest(request)),
            CaseActionRequestReviewed review => Serialize(review, CaseEventType.CaseActionRequestReviewed,
                MapReview(review)),
            CaseResolved resolution => Serialize(resolution, CaseEventType.CaseResolved,
                new CaseResolvedPayload(resolution.Reason)),
            CaseReopened reopening => Serialize(reopening, CaseEventType.CaseReopened,
                new CaseReopenedPayload(reopening.Reason, reopening.AccountableOwnerId)),
            CaseObservationRecorded observation => Serialize(observation, CaseEventType.CaseObservationRecorded,
                new CaseObservationRecordedPayload(observation.ObservationId, observation.Narrative, observation.Source)),
            CaseFollowUpScheduled scheduled => Serialize(scheduled, CaseEventType.CaseFollowUpScheduled,
                new CaseFollowUpScheduledPayload(scheduled.FollowUpId, scheduled.Purpose, scheduled.AssigneeActorId, NormalizeTimestamp(scheduled.DueAtUtc))),
            CaseFollowUpAcknowledged acknowledgement => Serialize(acknowledgement, CaseEventType.CaseFollowUpAcknowledged,
                new CaseFollowUpAcknowledgedPayload(acknowledgement.FollowUpId)),
            CaseFollowUpOutcomeRecorded outcome => Serialize(outcome, CaseEventType.CaseFollowUpOutcomeRecorded,
                MapFollowUpOutcome(outcome)),
            ActionOwnerChanged ownerChange => Serialize(ownerChange, CaseEventType.ActionOwnerChanged,
                new ActionOwnerChangedPayload(ownerChange.ActionKind, ownerChange.ActionId, ownerChange.PreviousOwnerId, ownerChange.NewOwnerId, ownerChange.Reason)),
            CaseEscalationRaised raised => Serialize(raised, CaseEventType.CaseEscalationRaised,
                new CaseEscalationRaisedPayload(raised.EscalationId, raised.Reason, Array.AsReadOnly(raised.EvidenceEventIds.ToArray()), raised.RecipientRoute)),
            CaseEscalationAcknowledged acknowledged => Serialize(acknowledged, CaseEventType.CaseEscalationAcknowledged,
                new CaseEscalationAcknowledgedPayload(acknowledged.EscalationId, acknowledged.ActionSummary, acknowledged.Reason)),
            CaseEscalationActionRecorded action => Serialize(action, CaseEventType.CaseEscalationActionRecorded,
                new CaseEscalationActionRecordedPayload(action.EscalationId, action.ActionSummary, action.Reason)),
            CaseEscalationClosed closed => Serialize(closed, CaseEventType.CaseEscalationClosed,
                new CaseEscalationClosedPayload(closed.EscalationId, closed.Resolution, closed.Reason)),
            _ => throw new NotSupportedException($"Event type {@event.GetType().Name} is not supported by the case-event v1 contract.")
        };
    }

    public int GetSchemaVersion(CaseEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return @event switch
        {
            CaseReported { ReportData: CaseReportEventData.HumanV2 or CaseReportEventData.AnimalV2 } => 2,
            CaseReportCorrected { CorrectedReportData: CaseReportEventData.HumanV2 or CaseReportEventData.AnimalV2 } => 2,
            CaseReported or CaseAssigned or CaseAssignmentAcknowledged or CaseReportCorrected or CaseActionRequested or CaseActionRequestReviewed or CaseResolved or CaseReopened
            or CaseObservationRecorded or CaseFollowUpScheduled or CaseFollowUpAcknowledged or CaseFollowUpOutcomeRecorded
            or CaseEscalationRaised or CaseEscalationAcknowledged or CaseEscalationActionRecorded or CaseEscalationClosed
            or CaseCollaboratorGranted or CaseCollaboratorRevoked or AssistanceRequested or AssistanceActivityRecorded or ActionOwnerChanged => 1,
            _ => throw new NotSupportedException($"Event type {@event.GetType().Name} is not supported by the case-event wire contract.")
        };
    }

    private static byte[] SerializeAssistanceActivity(AssistanceActivityRecorded activity)
    {
        var type = activity switch
        {
            AssistanceAccepted when activity.Status == AssistanceStatus.Accepted => CaseEventType.AssistanceAccepted,
            AssistanceProgressRecorded when activity.Status == AssistanceStatus.InProgress => CaseEventType.AssistanceProgressRecorded,
            AssistanceProvided when activity.Status == AssistanceStatus.Provided => CaseEventType.AssistanceProvided,
            AssistanceReceiptConfirmed when activity.Status == AssistanceStatus.ReceiptConfirmed => CaseEventType.AssistanceReceiptConfirmed,
            AssistanceUnfulfilled when activity.Status == AssistanceStatus.Unfulfilled => CaseEventType.AssistanceUnfulfilled,
            AssistanceCancelled when activity.Status == AssistanceStatus.Cancelled => CaseEventType.AssistanceCancelled,
            _ => throw new InvalidDataException("Assistance event type and status do not match.")
        };
        if (activity is AssistanceReceiptConfirmed receipt)
        {
            if (string.IsNullOrWhiteSpace(receipt.ReceiptMethod) || string.IsNullOrWhiteSpace(receipt.ReceiptContext))
                throw new InvalidDataException("Receipt provenance is required.");
            return Serialize(receipt, type, new AssistanceReceiptPayload(receipt.AssistanceId, receipt.Note, receipt.ReceiptMethod, receipt.ReceiptContext));
        }
        if (activity.ReceiptMethod is not null || activity.ReceiptContext is not null)
            throw new InvalidDataException("Receipt provenance cannot be recorded as other assistance activity.");
        return Serialize(activity, type, new AssistanceActivityPayload(activity.AssistanceId, activity.Note));
    }

    private static byte[] SerializeReported(CaseReported reported) => reported.ReportData switch
    {
        CaseReportEventData.LegacyV1 legacy => Serialize(reported, CaseEventType.CaseReported,
            new CaseReportedPayload(reported.WorkspaceId, MapReport(legacy.Report))),
        CaseReportEventData.HumanV2 or CaseReportEventData.AnimalV2 => Serialize(reported, CaseEventType.CaseReported,
            new CaseReportedV2Payload(reported.WorkspaceId, MapReportV2(reported.ReportData)), 2),
        _ => throw new InvalidDataException("The report payload variant is unsupported.")
    };

    private static byte[] SerializeCorrection(CaseReportCorrected correction) => correction.CorrectedReportData switch
    {
        CaseReportEventData.LegacyV1 legacy => Serialize(correction, CaseEventType.CaseReportCorrected,
            new CaseReportCorrectedPayload(MapReport(legacy.Report), correction.Reason, correction.PreviousReportRevision)),
        CaseReportEventData.HumanV2 or CaseReportEventData.AnimalV2 => Serialize(correction, CaseEventType.CaseReportCorrected,
            new CaseReportCorrectedV2Payload(MapReportV2(correction.CorrectedReportData), correction.Reason, correction.PreviousReportRevision), 2),
        _ => throw new InvalidDataException("The report payload variant is unsupported.")
    };

    private static byte[] Serialize<TPayload>(CaseEvent @event, CaseEventType eventType, TPayload payload)
        where TPayload : CaseEventPayload => Serialize(@event, eventType, payload, 1);

    private static byte[] Serialize<TPayload>(CaseEvent @event, CaseEventType eventType, TPayload payload, int schemaVersion)
        where TPayload : CaseEventPayload
    {
        var envelope = new CaseEventEnvelope<TPayload>(
            SchemaVersion: schemaVersion,
            EventId: @event.EventId,
            EventType: eventType,
            CaseId: @event.CaseId,
            AggregateVersion: @event.AggregateVersion,
            ReportRevision: @event.ReportRevision,
            CommandId: @event.CommandId,
            ActorId: @event.ActorId,
            OccurredAtUtc: @event.OccurredAtUtc,
            RecordedAtUtc: @event.RecordedAtUtc,
            Payload: payload);

        var serialized = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        serialized = Environment.NewLine == "\r\n"
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(serialized).Replace("\r\n", "\n", StringComparison.Ordinal))
            : serialized;
        Array.Resize(ref serialized, serialized.Length + 1);
        serialized[^1] = (byte)'\n';
        return serialized;
    }

    private static CaseReportPayload MapReport(ReportFacts report)
    {
        if (!Enum.IsDefined(report.Kind) || !Enum.IsDefined(report.Sector))
        {
            throw new InvalidDataException("Case report values are outside the case-event v1 contract.");
        }

        return new CaseReportPayload(
            report.Kind,
            report.Sector,
            report.Narrative,
            Array.AsReadOnly(report.Observations.ToArray()));
    }

    private static CaseReportV2Payload MapReportV2(CaseReportEventData reportData)
    {
        return reportData switch
        {
            CaseReportEventData.HumanV2 human => MapHumanReportV2(human),
            CaseReportEventData.AnimalV2 animal => MapAnimalReportV2(animal),
            _ => throw new InvalidDataException("Only typed v2 reports can use the v2 wire schema.")
        };
    }

    private static CaseReportV2Payload MapHumanReportV2(CaseReportEventData.HumanV2 human)
    {
        human.Validate();
        return new CaseReportV2Payload(
                "Human", human.Report.ReportId, MapContext(human.Report.Context), MapEvidence(human.Report.Evidence),
                MapConfirmations(human.Confirmations),
                new HumanReportV2Payload(MapFact(human.Report.PseudonymousSubjectReference), MapFact(human.Report.AgeYears), MapFact(human.Report.ReportedConcern)),
                null);
    }

    private static CaseReportV2Payload MapAnimalReportV2(CaseReportEventData.AnimalV2 animal)
    {
        animal.Validate();
        if (!Enum.IsDefined(animal.Report.SubjectScope)) throw new InvalidDataException("Animal subject scope is unsupported.");
        return new CaseReportV2Payload(
                "Animal", animal.Report.ReportId, MapContext(animal.Report.Context), MapEvidence(animal.Report.Evidence),
                MapConfirmations(animal.Confirmations), null,
                new AnimalReportV2Payload(MapSubjectScope(animal.Report.SubjectScope),
                    MapFact(animal.Report.Species), MapFact(animal.Report.HoldingReference),
                    MapCount(animal.Report.AnimalsAtRisk), MapCount(animal.Report.AnimalsAffected), MapCount(animal.Report.AnimalsDead)));
    }

    private static ReportContextV2Payload MapContext(ReportContext context)
    {
        context.Validate();
        if (!Enum.IsDefined(context.CapturePrecision) || !Enum.IsDefined(context.Location.Source) || !Enum.IsDefined(context.Location.Precision))
            throw new InvalidDataException("Report context contains an unsupported enum value.");
        return new ReportContextV2Payload(MapFact(context.Occurrence), context.CapturedAtUtc, context.CapturedOffset,
            context.CapturedZoneId, context.CapturePrecision, context.Location);
    }

    private static ReportEvidenceV2Payload MapEvidence(ReportEvidence evidence) => new(
        evidence.Narrative is null ? null : MapText(evidence.Narrative),
        Array.AsReadOnly(evidence.Observations.Select(MapText).ToArray()),
        evidence.OriginalAudio is null ? null : new ReportEvidenceReferenceV2Payload(
            evidence.OriginalAudio.Reference, MapProvenance(evidence.OriginalAudio.Provenance)));

    private static ReportConfirmationsV2Payload MapConfirmations(ReportConfirmations confirmations) => new(
        confirmations.ReportReviewConfirmed, confirmations.OccurrenceCountryCode, confirmations.OccurrenceCountryCatalogueVersion,
        confirmations.OccurrenceCountryConfirmed, confirmations.ProcessingConfirmed, confirmations.ProcessingPolicyReference,
        confirmations.ProcessingPolicyVersion, confirmations.RecordingConfirmed, confirmations.RecordingPolicyReference,
        confirmations.RecordingPolicyVersion);

    private static ReportedFactV2Payload<T> MapFact<T>(ReportedFact<T> fact)
    {
        fact.Validate();
        if (!Enum.IsDefined(fact.Value.State) || !Enum.IsDefined(fact.Provenance.Source))
            throw new InvalidDataException("Reported fact contains an unsupported enum value.");
        return new ReportedFactV2Payload<T>(MapValueState(fact.Value.State),
            fact.Value.HasValue ? JsonSerializer.SerializeToElement(fact.Value.Value, JsonOptions) : null, MapProvenance(fact.Provenance));
    }

    private static AnimalCountV2Payload MapCount(AnimalCount count)
    {
        count.Validate();
        if (!Enum.IsDefined(count.Value.State) || !Enum.IsDefined(count.Provenance.Source))
            throw new InvalidDataException("Reported count contains an unsupported enum value.");
        return new AnimalCountV2Payload(MapValueState(count.Value.State),
            count.Value.HasValue ? count.Value.Value : null, MapProvenance(count.Provenance), count.PopulationReference,
            count.ObservationPeriod is null ? null : MapFact(count.ObservationPeriod));
    }

    private static ReportedTextV2Payload MapText(ReportedText text)
    {
        text.Validate();
        return new ReportedTextV2Payload(text.Text, MapProvenance(text.Provenance));
    }

    private static ReportProvenanceV2Payload MapProvenance(ReportProvenance provenance)
    {
        provenance.Validate();
        return new ReportProvenanceV2Payload(provenance.Source switch
        {
            ReportSourceKind.ReporterNarrative => ReportSourceV2Kind.ReporterNarrative,
            ReportSourceKind.ReporterObservation => ReportSourceV2Kind.ReporterObservation,
            ReportSourceKind.OriginalAudio => ReportSourceV2Kind.OriginalAudio,
            ReportSourceKind.WorkerConfirmed => ReportSourceV2Kind.WorkerConfirmed,
            _ => throw new InvalidDataException("Report provenance source is unsupported.")
        }, provenance.StableReference);
    }

    private static ReportedValueV2State MapValueState(ReportedValueState state) => state switch
    {
        ReportedValueState.Unknown => ReportedValueV2State.Unknown,
        ReportedValueState.NotRecorded => ReportedValueV2State.NotRecorded,
        ReportedValueState.NotApplicable => ReportedValueV2State.NotApplicable,
        ReportedValueState.Absent => ReportedValueV2State.Absent,
        ReportedValueState.Known => ReportedValueV2State.Known,
        _ => throw new InvalidDataException("Reported value state is unsupported.")
    };

    private static AnimalSubjectV2Scope MapSubjectScope(AnimalSubjectScope scope) => scope switch
    {
        AnimalSubjectScope.Individual => AnimalSubjectV2Scope.Individual,
        AnimalSubjectScope.Herd => AnimalSubjectV2Scope.Herd,
        AnimalSubjectScope.Flock => AnimalSubjectV2Scope.Flock,
        AnimalSubjectScope.OtherGroup => AnimalSubjectV2Scope.OtherGroup,
        _ => throw new InvalidDataException("Animal subject scope is unsupported.")
    };

    private static CaseActionRequestedPayload MapRequest(CaseActionRequested request)
    {
        if (!Enum.IsDefined(request.RequestedOperation))
        {
            throw new InvalidDataException("The requested operation is outside the case-event v1 contract.");
        }

        return new CaseActionRequestedPayload(request.RequestId, request.RequestedOperation, request.Reason,
            Array.AsReadOnly(request.EvidenceEventIds.ToArray()), request.ProposedOwnerId);
    }

    private static CaseFollowUpOutcomeRecordedPayload MapFollowUpOutcome(CaseFollowUpOutcomeRecorded outcome)
    {
        if (!Enum.IsDefined(outcome.Outcome))
        {
            throw new InvalidDataException("The follow-up outcome is outside the case-event v1 contract.");
        }

        DateTimeOffset? newDue = outcome.NewDueAtUtc is null ? null : NormalizeTimestamp(outcome.NewDueAtUtc.Value);
        return new CaseFollowUpOutcomeRecordedPayload(
            outcome.FollowUpId,
            outcome.Outcome,
            outcome.OutcomeNotes,
            NormalizeTimestamp(outcome.PreviousDueAtUtc),
            newDue,
            outcome.Reason);
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value) => value.ToUniversalTime();

    private static CaseActionRequestReviewedPayload MapReview(CaseActionRequestReviewed review)
    {
        if (!Enum.IsDefined(review.Outcome))
        {
            throw new InvalidDataException("The review outcome is outside the case-event v1 contract.");
        }

        var approved = review.Outcome == CaseActionReviewOutcome.Approved;
        if (approved != (review.LinkedAcceptedEventId is not null))
        {
            throw new InvalidDataException("Approved reviews require an accepted-event link; other outcomes must not have one.");
        }

        return new CaseActionRequestReviewedPayload(review.RequestId, review.Outcome, review.LinkedAcceptedEventId, review.Reason);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
