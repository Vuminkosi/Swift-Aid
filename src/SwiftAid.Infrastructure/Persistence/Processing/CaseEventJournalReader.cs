using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Events;
using SwiftAid.Contracts.Geography;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Infrastructure.Persistence.Processing;

/// <summary>Strictly validates protected v1 envelopes against their SQL event metadata.</summary>
internal static class CaseEventJournalReader
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static CaseEvent Read(CaseEventEntity row, ReadOnlyMemory<byte> serialized)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (serialized.IsEmpty)
        {
            throw InvalidJournal();
        }

        try
        {
            using var document = JsonDocument.Parse(serialized);
            RejectDuplicateProperties(document.RootElement);
            var root = document.RootElement;
            RequireProperties(root,
            [
                "schemaVersion", "eventId", "eventType", "caseId", "aggregateVersion", "reportRevision",
                "commandId", "actorId", "occurredAtUtc", "recordedAtUtc", "payload"
            ]);

            var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
            if (schemaVersion is not (1 or 2) || root.GetProperty("eventType").ValueKind != JsonValueKind.String)
            {
                throw InvalidJournal();
            }

            RequireCanonicalGuid(root, "eventId");
            RequireCanonicalGuid(root, "caseId");
            RequireCanonicalGuid(root, "commandId");

            var eventType = root.GetProperty("eventType").GetString();
            if (!string.Equals(eventType, row.EventType, StringComparison.Ordinal))
            {
                throw InvalidJournal();
            }

            var result = schemaVersion == 2
                ? eventType switch
                {
                    nameof(CaseEventType.CaseReported) => ReadV2<CaseReportedV2Payload>(serialized,
                        ["workspaceId", "report"], ValidateReportedV2Payload),
                    nameof(CaseEventType.CaseReportCorrected) => ReadV2<CaseReportCorrectedV2Payload>(serialized,
                        ["report", "reason", "previousReportRevision"], ValidateCorrectionV2Payload),
                    _ => throw InvalidJournal()
                }
                : eventType switch
            {
                nameof(CaseEventType.AssistanceRequested) => Read<AssistanceRequestedPayload>(serialized,
                    ["assistanceId", "need", "responsibleActorId", "dueAtUtc"], (payload, value) =>
                    {
                        ValidateAssistanceIdentity(payload, value.AssistanceId);
                        RequireText(value.Need);
                        RequireIdentifier(value.ResponsibleActorId);
                        if (value.DueAtUtc is { Offset: var offset } && offset != TimeSpan.Zero) throw InvalidJournal();
                    }),
                nameof(CaseEventType.AssistanceReceiptConfirmed) => Read<AssistanceReceiptPayload>(serialized,
                    ["assistanceId", "note", "receiptMethod", "receiptContext"], (payload, value) =>
                    {
                        ValidateAssistanceIdentity(payload, value.AssistanceId);
                        RequireText(value.Note);
                        RequireText(value.ReceiptMethod);
                        RequireText(value.ReceiptContext);
                    }),
                nameof(CaseEventType.AssistanceAccepted) => Read<AssistanceActivityPayload>(serialized,
                    ["assistanceId", "note"], (payload, value) => { ValidateAssistanceIdentity(payload, value.AssistanceId); RequireText(value.Note); }),
                nameof(CaseEventType.AssistanceProgressRecorded) => Read<AssistanceActivityPayload>(serialized,
                    ["assistanceId", "note"], (payload, value) => { ValidateAssistanceIdentity(payload, value.AssistanceId); RequireText(value.Note); }),
                nameof(CaseEventType.AssistanceProvided) => Read<AssistanceActivityPayload>(serialized,
                    ["assistanceId", "note"], (payload, value) => { ValidateAssistanceIdentity(payload, value.AssistanceId); RequireText(value.Note); }),
                nameof(CaseEventType.AssistanceUnfulfilled) => Read<AssistanceActivityPayload>(serialized,
                    ["assistanceId", "note"], (payload, value) => { ValidateAssistanceIdentity(payload, value.AssistanceId); RequireText(value.Note); }),
                nameof(CaseEventType.AssistanceCancelled) => Read<AssistanceActivityPayload>(serialized,
                    ["assistanceId", "note"], (payload, value) => { ValidateAssistanceIdentity(payload, value.AssistanceId); RequireText(value.Note); }),
                nameof(CaseEventType.CaseReported) => Read<CaseReportedPayload>(serialized,
                    ["workspaceId", "report"], ValidateReportPayload),
                nameof(CaseEventType.CaseAssigned) => Read<CaseAssignedPayload>(serialized,
                    ["accountableOwnerId"], ValidateAssignedPayload),
                nameof(CaseEventType.CaseCollaboratorGranted) => Read<CaseCollaboratorGrantedPayload>(serialized,
                    ["grantId", "collaboratorActorId", "actions", "expiresAtUtc", "reason"], ValidateCollaboratorGranted),
                nameof(CaseEventType.CaseCollaboratorRevoked) => Read<CaseCollaboratorRevokedPayload>(serialized,
                    ["grantId", "reason"], (payload, value) =>
                    {
                        RequireCanonicalGuid(payload, "grantId");
                        if (value.GrantId == Guid.Empty) throw InvalidJournal();
                        RequireText(value.Reason);
                    }),
                nameof(CaseEventType.CaseAssignmentAcknowledged) => Read<CaseAssignmentAcknowledgedPayload>(serialized,
                    ["assignmentVersion"], (payload, value) =>
                    {
                        if (payload.GetProperty("assignmentVersion").ValueKind != JsonValueKind.Number || value.AssignmentVersion <= 0)
                            throw InvalidJournal();
                    }),
                nameof(CaseEventType.CaseReportCorrected) => Read<CaseReportCorrectedPayload>(serialized,
                    ["report", "reason", "previousReportRevision"], ValidateCorrectionPayload),
                nameof(CaseEventType.CaseActionRequested) => Read<CaseActionRequestedPayload>(serialized,
                    ["requestId", "requestedOperation", "reason", "evidenceEventIds", "proposedOwnerId"], ValidateRequestPayload),
                nameof(CaseEventType.CaseActionRequestReviewed) => Read<CaseActionRequestReviewedPayload>(serialized,
                    ["requestId", "outcome", "linkedAcceptedEventId", "reason"], ValidateReviewPayload),
                nameof(CaseEventType.CaseResolved) => Read<CaseResolvedPayload>(serialized,
                    ["reason"], ValidateReasonPayload),
                nameof(CaseEventType.CaseReopened) => Read<CaseReopenedPayload>(serialized,
                    ["reason", "accountableOwnerId"], ValidateReopenedPayload),
                nameof(CaseEventType.CaseObservationRecorded) => Read<CaseObservationRecordedPayload>(serialized,
                    ["observationId", "narrative", "source"], ValidateObservationPayload),
                nameof(CaseEventType.CaseFollowUpScheduled) => Read<CaseFollowUpScheduledPayload>(serialized,
                    ["followUpId", "purpose", "assigneeActorId", "dueAtUtc"], ValidateFollowUpScheduledPayload),
                nameof(CaseEventType.CaseFollowUpAcknowledged) => Read<CaseFollowUpAcknowledgedPayload>(serialized,
                    ["followUpId"], ValidateFollowUpAcknowledgedPayload),
                nameof(CaseEventType.CaseFollowUpOutcomeRecorded) => Read<CaseFollowUpOutcomeRecordedPayload>(serialized,
                    ["followUpId", "outcome", "outcomeNotes", "previousDueAtUtc", "newDueAtUtc", "reason"], ValidateFollowUpOutcomePayload),
                nameof(CaseEventType.ActionOwnerChanged) => Read<ActionOwnerChangedPayload>(serialized,
                    ["actionKind", "actionId", "previousOwnerId", "newOwnerId", "reason"], ValidateActionOwnerChangedPayload),
                nameof(CaseEventType.CaseEscalationRaised) => Read<CaseEscalationRaisedPayload>(serialized,
                    ["escalationId", "reason", "evidenceEventIds", "recipientRoute"], ValidateEscalationRaisedPayload),
                nameof(CaseEventType.CaseEscalationAcknowledged) => Read<CaseEscalationAcknowledgedPayload>(serialized,
                    ["escalationId", "actionSummary", "reason"], ValidateEscalationAcknowledgedPayload),
                nameof(CaseEventType.CaseEscalationActionRecorded) => Read<CaseEscalationActionRecordedPayload>(serialized,
                    ["escalationId", "actionSummary", "reason"], ValidateEscalationActionRecordedPayload),
                nameof(CaseEventType.CaseEscalationClosed) => Read<CaseEscalationClosedPayload>(serialized,
                    ["escalationId", "resolution", "reason"], ValidateEscalationClosedPayload),
                _ => throw InvalidJournal()
            };

            ValidateRowAgreement(row, result, schemaVersion);
            return result;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException or NullReferenceException or KeyNotFoundException)
        {
            throw InvalidJournal();
        }
    }

    private static CaseEvent ReadV2<TPayload>(
        ReadOnlyMemory<byte> serialized,
        string[] payloadProperties,
        Action<JsonElement, TPayload> validatePayload)
        where TPayload : CaseEventPayload
    {
        using (var document = JsonDocument.Parse(serialized))
        {
            var payloadElement = document.RootElement.GetProperty("payload");
            RequireProperties(payloadElement, payloadProperties);
            ValidateKnownReportedTimesV2(payloadElement);
            var value = JsonSerializer.Deserialize<TPayload>(payloadElement, JsonOptions) ?? throw InvalidJournal();
            validatePayload(payloadElement, value);
        }

        var envelope = JsonSerializer.Deserialize<CaseEventEnvelope<TPayload>>(serialized.Span, JsonOptions)
            ?? throw InvalidJournal();
        var payload = envelope.Payload ?? throw InvalidJournal();
        return (envelope.EventType, payload) switch
        {
            (CaseEventType.CaseReported, CaseReportedV2Payload reported) => new CaseReported(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                ToDomainReportV2(reported.Report), reported.WorkspaceId),
            (CaseEventType.CaseReportCorrected, CaseReportCorrectedV2Payload corrected) => new CaseReportCorrected(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                ToDomainReportV2(corrected.Report), corrected.Reason, corrected.PreviousReportRevision),
            _ => throw InvalidJournal()
        };
    }

    private static void ValidateKnownReportedTimesV2(JsonElement payload)
    {
        if (!payload.TryGetProperty("report", out var report) || report.ValueKind != JsonValueKind.Object ||
            !report.TryGetProperty("context", out var context) || context.ValueKind != JsonValueKind.Object ||
            !context.TryGetProperty("occurrence", out var occurrence))
        {
            throw InvalidJournal();
        }

        ValidateKnownReportedTimeFact(occurrence);
        if (!report.TryGetProperty("reportKind", out var reportKind) || reportKind.ValueKind != JsonValueKind.String ||
            !string.Equals(reportKind.GetString(), "Animal", StringComparison.Ordinal) ||
            !report.TryGetProperty("animal", out var animal) || animal.ValueKind != JsonValueKind.Object ||
            !animal.TryGetProperty("animalsAtRisk", out var animalsAtRisk) ||
            !animal.TryGetProperty("animalsAffected", out var animalsAffected) ||
            !animal.TryGetProperty("animalsDead", out var animalsDead))
        {
            return;
        }

        ValidateAnimalObservationPeriod(animalsAtRisk);
        ValidateAnimalObservationPeriod(animalsAffected);
        ValidateAnimalObservationPeriod(animalsDead);
    }

    private static void ValidateAnimalObservationPeriod(JsonElement animalCount)
    {
        if (animalCount.ValueKind != JsonValueKind.Object || !animalCount.TryGetProperty("observationPeriod", out var period))
        {
            return;
        }

        if (period.ValueKind == JsonValueKind.Object) ValidateKnownReportedTimeFact(period);
    }

    private static void ValidateKnownReportedTimeFact(JsonElement reportedTimeFact)
    {
        if (reportedTimeFact.ValueKind != JsonValueKind.Object ||
            !reportedTimeFact.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<ReportedValueV2State>(state.GetString(), ignoreCase: true, out var parsedState) ||
            parsedState != ReportedValueV2State.Known)
        {
            return;
        }

        if (!reportedTimeFact.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidJournal();
        }

        RequireProperties(value, ["value", "localDate", "rangeStart", "rangeEnd", "originalOffset", "zoneId", "precision"]);
        if (!value.TryGetProperty("precision", out var precision) || precision.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<ReportedTimePrecision>(precision.GetString(), ignoreCase: false, out var parsedPrecision) ||
            !Enum.IsDefined(parsedPrecision) || !string.Equals(precision.GetString(), parsedPrecision.ToString(), StringComparison.Ordinal))
        {
            throw InvalidJournal();
        }
    }

    private static CaseEvent Read<TPayload>(
        ReadOnlyMemory<byte> serialized,
        string[] payloadProperties,
        Action<JsonElement, TPayload> validatePayload)
        where TPayload : CaseEventPayload
    {
        using (var document = JsonDocument.Parse(serialized))
        {
            var payloadElement = document.RootElement.GetProperty("payload");
            RequireProperties(payloadElement, payloadProperties);
            ValidateNestedPayload(payloadElement, validatePayload);
        }

        var envelope = JsonSerializer.Deserialize<CaseEventEnvelope<TPayload>>(serialized.Span, JsonOptions)
            ?? throw InvalidJournal();
        var payload = envelope.Payload ?? throw InvalidJournal();
        return envelope.EventType switch
        {
            CaseEventType.CaseReported when payload is CaseReportedPayload reported => new CaseReported(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                ToDomainReport(reported.Report), reported.WorkspaceId),
            CaseEventType.CaseAssigned when payload is CaseAssignedPayload assigned => new CaseAssigned(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc, assigned.AccountableOwnerId),
            CaseEventType.CaseReportCorrected when payload is CaseReportCorrectedPayload corrected => new CaseReportCorrected(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                ToDomainReport(corrected.Report), corrected.Reason, corrected.PreviousReportRevision),
            CaseEventType.CaseActionRequested when payload is CaseActionRequestedPayload request => new CaseActionRequested(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                request.RequestId, request.RequestedOperation, request.Reason,
                Array.AsReadOnly(request.EvidenceEventIds.ToArray()), request.ProposedOwnerId),
            CaseEventType.CaseActionRequestReviewed when payload is CaseActionRequestReviewedPayload review => new CaseActionRequestReviewed(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                review.RequestId, review.Outcome, review.LinkedAcceptedEventId, review.Reason),
            CaseEventType.CaseResolved when payload is CaseResolvedPayload resolved => new CaseResolved(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc, resolved.Reason),
            CaseEventType.CaseReopened when payload is CaseReopenedPayload reopened => new CaseReopened(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                reopened.Reason, reopened.AccountableOwnerId),
            CaseEventType.CaseObservationRecorded when payload is CaseObservationRecordedPayload observation => new CaseObservationRecorded(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                observation.ObservationId, observation.Narrative, observation.Source),
            CaseEventType.CaseFollowUpScheduled when payload is CaseFollowUpScheduledPayload scheduled => new CaseFollowUpScheduled(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                scheduled.FollowUpId, scheduled.Purpose, scheduled.AssigneeActorId, scheduled.DueAtUtc),
            CaseEventType.ActionOwnerChanged when payload is ActionOwnerChangedPayload ownerChange => new ActionOwnerChanged(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc, ownerChange.ActionKind,
                ownerChange.ActionId, ownerChange.PreviousOwnerId, ownerChange.NewOwnerId, ownerChange.Reason),
            CaseEventType.CaseFollowUpAcknowledged when payload is CaseFollowUpAcknowledgedPayload acknowledgement => new CaseFollowUpAcknowledged(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc, acknowledgement.FollowUpId),
            CaseEventType.CaseFollowUpOutcomeRecorded when payload is CaseFollowUpOutcomeRecordedPayload outcome => new CaseFollowUpOutcomeRecorded(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                outcome.FollowUpId, outcome.Outcome, outcome.OutcomeNotes, outcome.PreviousDueAtUtc, outcome.NewDueAtUtc,
                outcome.Reason),
            CaseEventType.CaseEscalationRaised when payload is CaseEscalationRaisedPayload raised => new CaseEscalationRaised(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                raised.EscalationId, raised.Reason,
                Array.AsReadOnly(raised.EvidenceEventIds.ToArray()), raised.RecipientRoute),
            CaseEventType.CaseEscalationAcknowledged when payload is CaseEscalationAcknowledgedPayload acknowledged => new CaseEscalationAcknowledged(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                acknowledged.EscalationId, acknowledged.ActionSummary, acknowledged.Reason),
            CaseEventType.CaseEscalationActionRecorded when payload is CaseEscalationActionRecordedPayload action => new CaseEscalationActionRecorded(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                action.EscalationId, action.ActionSummary, action.Reason),
            CaseEventType.CaseEscalationClosed when payload is CaseEscalationClosedPayload closed => new CaseEscalationClosed(
                envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision, envelope.CommandId,
                envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                closed.EscalationId, closed.Resolution, closed.Reason),
            CaseEventType.AssistanceRequested when payload is AssistanceRequestedPayload requested =>
                new AssistanceRequested(envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision,
                    envelope.CommandId, envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                    requested.AssistanceId, requested.Need, requested.ResponsibleActorId, requested.DueAtUtc),
            CaseEventType.AssistanceReceiptConfirmed when payload is AssistanceReceiptPayload receipt =>
                new AssistanceReceiptConfirmed(envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision,
                    envelope.CommandId, envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                    receipt.AssistanceId, receipt.Note, receipt.ReceiptMethod, receipt.ReceiptContext),
            CaseEventType.AssistanceAccepted when payload is AssistanceActivityPayload activity =>
                new AssistanceAccepted(envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision,
                    envelope.CommandId, envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc, activity.AssistanceId, activity.Note),
            CaseEventType.AssistanceProgressRecorded when payload is AssistanceActivityPayload activity =>
                new AssistanceProgressRecorded(envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision,
                    envelope.CommandId, envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc, activity.AssistanceId, activity.Note),
            CaseEventType.AssistanceProvided when payload is AssistanceActivityPayload activity =>
                new AssistanceProvided(envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision,
                    envelope.CommandId, envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc, activity.AssistanceId, activity.Note),
            CaseEventType.AssistanceUnfulfilled when payload is AssistanceActivityPayload activity =>
                new AssistanceUnfulfilled(envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision,
                    envelope.CommandId, envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc, activity.AssistanceId, activity.Note),
            CaseEventType.AssistanceCancelled when payload is AssistanceActivityPayload activity =>
                new AssistanceCancelled(envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision,
                    envelope.CommandId, envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc, activity.AssistanceId, activity.Note),
            CaseEventType.CaseCollaboratorGranted when payload is CaseCollaboratorGrantedPayload grant && grant.ExpiresAtUtc > envelope.RecordedAtUtc =>
                new CaseCollaboratorGranted(envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision,
                    envelope.CommandId, envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc,
                    grant.GrantId, grant.CollaboratorActorId, grant.Actions, grant.ExpiresAtUtc, grant.Reason),
            CaseEventType.CaseCollaboratorRevoked when payload is CaseCollaboratorRevokedPayload revoke =>
                new CaseCollaboratorRevoked(envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision,
                    envelope.CommandId, envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc, revoke.GrantId, revoke.Reason),
            CaseEventType.CaseAssignmentAcknowledged when payload is CaseAssignmentAcknowledgedPayload acknowledgement && acknowledgement.AssignmentVersion < envelope.AggregateVersion =>
                new CaseAssignmentAcknowledged(envelope.CaseId, envelope.AggregateVersion, envelope.ReportRevision,
                    envelope.CommandId, envelope.ActorId, envelope.OccurredAtUtc, envelope.RecordedAtUtc, acknowledgement.AssignmentVersion),
            _ => throw InvalidJournal()
        };
    }

    private static void ValidateAssistanceIdentity(JsonElement payload, Guid id)
    {
        RequireCanonicalGuid(payload, "assistanceId");
        if (id == Guid.Empty) throw InvalidJournal();
    }

    private static void ValidateCollaboratorGranted(JsonElement payload, CaseCollaboratorGrantedPayload value)
    {
        const CaseCollaborationActions allowed = CaseCollaborationActions.ReadCase | CaseCollaborationActions.ReadAttachment |
            CaseCollaborationActions.Synchronize | CaseCollaborationActions.RecordObservation |
            CaseCollaborationActions.ManageFollowUp | CaseCollaborationActions.ManageEscalation;
        RequireCanonicalGuid(payload, "grantId");
        RequireText(value.CollaboratorActorId);
        RequireText(value.Reason);
        if (value.GrantId == Guid.Empty || (value.Actions & ~allowed) != 0 ||
            !value.Actions.HasFlag(CaseCollaborationActions.ReadCase) ||
            payload.GetProperty("actions").ValueKind != JsonValueKind.String ||
            payload.GetProperty("actions").GetString() != value.Actions.ToString() ||
            payload.GetProperty("expiresAtUtc").ValueKind != JsonValueKind.String || value.ExpiresAtUtc.Offset != TimeSpan.Zero)
            throw InvalidJournal();
    }

    private static void ValidateNestedPayload<TPayload>(JsonElement payload, Action<JsonElement, TPayload> validate)
        where TPayload : CaseEventPayload
    {
        var value = JsonSerializer.Deserialize<TPayload>(payload, JsonOptions) ?? throw InvalidJournal();
        validate(payload, value);
    }

    private static void ValidateReportPayload(JsonElement payload, CaseReportedPayload value)
    {
        RequireIdentifier(value.WorkspaceId);
        ValidateReport(payload.GetProperty("report"), value.Report);
    }

    private static void ValidateCorrectionPayload(JsonElement payload, CaseReportCorrectedPayload value)
    {
        ValidateReport(payload.GetProperty("report"), value.Report);
        RequireText(value.Reason);
        if (value.PreviousReportRevision < 1)
        {
            throw InvalidJournal();
        }
    }

    private static void ValidateReportedV2Payload(JsonElement payload, CaseReportedV2Payload value)
    {
        RequireIdentifier(value.WorkspaceId);
        ValidateReportV2(payload.GetProperty("report"), value.Report);
    }

    private static void ValidateCorrectionV2Payload(JsonElement payload, CaseReportCorrectedV2Payload value)
    {
        ValidateReportV2(payload.GetProperty("report"), value.Report);
        RequireText(value.Reason);
        if (value.PreviousReportRevision < 1) throw InvalidJournal();
    }

    private static void ValidateReportV2(JsonElement element, CaseReportV2Payload? value)
    {
        RequireProperties(element, ["reportKind", "reportId", "context", "evidence", "confirmations", "human", "animal"]);
        if (value is null || !IsCanonicalGuid(element, "reportId") || value.ReportId == Guid.Empty)
            throw InvalidJournal();

        ValidateContextV2(element.GetProperty("context"), value.Context);
        ValidateEvidenceV2(element.GetProperty("evidence"), value.Evidence);
        ValidateConfirmationsV2(element.GetProperty("confirmations"), value.Confirmations);
        var audioPresent = value.Evidence.OriginalAudio is not null;
        if (audioPresent)
        {
            if (value.Confirmations.RecordingConfirmed is not true ||
                string.IsNullOrWhiteSpace(value.Confirmations.RecordingPolicyReference) ||
                string.IsNullOrWhiteSpace(value.Confirmations.RecordingPolicyVersion)) throw InvalidJournal();
        }
        else if (value.Confirmations.RecordingConfirmed is not null ||
            value.Confirmations.RecordingPolicyReference is not null || value.Confirmations.RecordingPolicyVersion is not null)
        {
            throw InvalidJournal();
        }
        if (value.ReportKind == "Human")
        {
            if (value.Human is null || value.Animal is not null) throw InvalidJournal();
            var human = element.GetProperty("human");
            RequireProperties(human, ["pseudonymousSubjectReference", "ageYears", "reportedConcern"]);
            ValidateFactV2(human.GetProperty("pseudonymousSubjectReference"), value.Human.PseudonymousSubjectReference);
            ValidateFactV2(human.GetProperty("ageYears"), value.Human.AgeYears);
            ValidateFactV2(human.GetProperty("reportedConcern"), value.Human.ReportedConcern);
            if (element.GetProperty("animal").ValueKind != JsonValueKind.Null) throw InvalidJournal();
        }
        else if (value.ReportKind == "Animal")
        {
            if (value.Animal is null || value.Human is not null) throw InvalidJournal();
            var animal = element.GetProperty("animal");
            RequireProperties(animal, ["subjectScope", "species", "holdingReference", "animalsAtRisk", "animalsAffected", "animalsDead"]);
            if (!IsExactEnumValue(animal, "subjectScope", value.Animal.SubjectScope)) throw InvalidJournal();
            ValidateFactV2(animal.GetProperty("species"), value.Animal.Species);
            ValidateFactV2(animal.GetProperty("holdingReference"), value.Animal.HoldingReference);
            ValidateCountV2(animal.GetProperty("animalsAtRisk"), value.Animal.AnimalsAtRisk);
            ValidateCountV2(animal.GetProperty("animalsAffected"), value.Animal.AnimalsAffected);
            ValidateCountV2(animal.GetProperty("animalsDead"), value.Animal.AnimalsDead);
            if (element.GetProperty("human").ValueKind != JsonValueKind.Null) throw InvalidJournal();
        }
        else throw InvalidJournal();
    }

    private static void ValidateContextV2(JsonElement element, ReportContextV2Payload? context)
    {
        RequireProperties(element, ["occurrence", "capturedAtUtc", "capturedOffset", "capturedZoneId", "capturePrecision", "location"]);
        if (context is null || context.Occurrence is null || context.Location is null ||
            !IsExactEnumValue(element, "capturePrecision", context.CapturePrecision) ||
            !Enum.IsDefined(context.CapturePrecision) ||
            !Enum.IsDefined(context.Location.Source) || !Enum.IsDefined(context.Location.Precision)) throw InvalidJournal();
        ValidateFactV2(element.GetProperty("occurrence"), context.Occurrence);
        var location = element.GetProperty("location");
        RequireProperties(location, ["countryCode", "region", "area", "locality", "latitude", "longitude", "accuracyMetres", "source", "precision", "catalogueVersion"]);
        if (!IsExactEnumValue(location, "source", context.Location.Source) || !IsExactEnumValue(location, "precision", context.Location.Precision)) throw InvalidJournal();
        ValidatePlaceV2(location.GetProperty("region"), context.Location.Region);
        ValidatePlaceV2(location.GetProperty("area"), context.Location.Area);
        ValidatePlaceV2(location.GetProperty("locality"), context.Location.Locality);
    }

    private static void ValidatePlaceV2(JsonElement element, OptionalPlace? place)
    {
        RequireProperties(element, ["reportedName", "normalizedId", "presence"]);
        if (place is null || !IsExactEnumValue(element, "presence", place.Presence)) throw InvalidJournal();
    }

    private static void ValidateEvidenceV2(JsonElement element, ReportEvidenceV2Payload? evidence)
    {
        RequireProperties(element, ["narrative", "observations", "originalAudio"]);
        if (evidence is null || evidence.Observations is null) throw InvalidJournal();
        var narrative = element.GetProperty("narrative");
        if (evidence.Narrative is null)
        {
            if (narrative.ValueKind != JsonValueKind.Null) throw InvalidJournal();
        }
        else ValidateTextV2(narrative, evidence.Narrative);

        var observations = element.GetProperty("observations");
        if (observations.ValueKind != JsonValueKind.Array || observations.GetArrayLength() != evidence.Observations.Count) throw InvalidJournal();
        for (var index = 0; index < evidence.Observations.Count; index++) ValidateTextV2(observations[index], evidence.Observations[index]);

        var audio = element.GetProperty("originalAudio");
        if (evidence.OriginalAudio is null)
        {
            if (audio.ValueKind != JsonValueKind.Null) throw InvalidJournal();
        }
        else
        {
            RequireProperties(audio, ["reference", "provenance"]);
            RequireText(evidence.OriginalAudio.Reference);
            ValidateProvenanceV2(audio.GetProperty("provenance"), evidence.OriginalAudio.Provenance);
            if (evidence.OriginalAudio.Provenance.Source != ReportSourceV2Kind.OriginalAudio) throw InvalidJournal();
        }
        if (evidence.Narrative is null && evidence.Observations.Count == 0 && evidence.OriginalAudio is null) throw InvalidJournal();
    }

    private static void ValidateTextV2(JsonElement element, ReportedTextV2Payload? text)
    {
        RequireProperties(element, ["text", "provenance"]);
        if (text is null) throw InvalidJournal();
        RequireText(text.Text);
        ValidateProvenanceV2(element.GetProperty("provenance"), text.Provenance);
    }

    private static void ValidateFactV2<T>(JsonElement element, ReportedFactV2Payload<T>? fact)
    {
        RequireProperties(element, ["state", "value", "provenance"]);
        if (fact is null || !IsExactEnumValue(element, "state", fact.State) || !Enum.IsDefined(fact.State)) throw InvalidJournal();
        var value = element.GetProperty("value");
        if ((fact.State == ReportedValueV2State.Known) != (value.ValueKind != JsonValueKind.Null) ||
            (fact.State == ReportedValueV2State.Known) != fact.Value.HasValue) throw InvalidJournal();
        ValidateProvenanceV2(element.GetProperty("provenance"), fact.Provenance);
    }

    private static void ValidateProvenanceV2(JsonElement element, ReportProvenanceV2Payload? provenance)
    {
        RequireProperties(element, ["source", "stableReference"]);
        if (provenance is null || !IsExactEnumValue(element, "source", provenance.Source) || !Enum.IsDefined(provenance.Source)) throw InvalidJournal();
        RequireText(provenance.StableReference);
    }

    private static void ValidateCountV2(JsonElement element, AnimalCountV2Payload? count)
    {
        RequireProperties(element, ["state", "value", "provenance", "populationReference", "observationPeriod"]);
        if (count is null || !IsExactEnumValue(element, "state", count.State) || !Enum.IsDefined(count.State)) throw InvalidJournal();
        if ((count.State == ReportedValueV2State.Known) != (element.GetProperty("value").ValueKind != JsonValueKind.Null) ||
            (count.State == ReportedValueV2State.Known) != count.Value.HasValue || (count.Value < 0)) throw InvalidJournal();
        ValidateProvenanceV2(element.GetProperty("provenance"), count.Provenance);
        if (count.ObservationPeriod is null)
        {
            if (element.GetProperty("observationPeriod").ValueKind != JsonValueKind.Null) throw InvalidJournal();
        }
        else ValidateFactV2(element.GetProperty("observationPeriod"), count.ObservationPeriod);
    }

    private static void ValidateConfirmationsV2(JsonElement element, ReportConfirmationsV2Payload? confirmations)
    {
        RequireProperties(element, ["reportReviewConfirmed", "occurrenceCountryCode", "occurrenceCountryCatalogueVersion", "occurrenceCountryConfirmed", "processingConfirmed", "processingPolicyReference", "processingPolicyVersion", "recordingConfirmed", "recordingPolicyReference", "recordingPolicyVersion"]);
        if (confirmations is null || !confirmations.ReportReviewConfirmed || !confirmations.OccurrenceCountryConfirmed || !confirmations.ProcessingConfirmed ||
            string.IsNullOrWhiteSpace(confirmations.OccurrenceCountryCode) || string.IsNullOrWhiteSpace(confirmations.OccurrenceCountryCatalogueVersion) ||
            string.IsNullOrWhiteSpace(confirmations.ProcessingPolicyReference) || string.IsNullOrWhiteSpace(confirmations.ProcessingPolicyVersion)) throw InvalidJournal();
    }

    private static void ValidateReport(JsonElement element, CaseReportPayload? value)
    {
        RequireProperties(element, ["kind", "sector", "narrative", "observations"]);
        if (value is null || !IsExactEnumValue(element, "kind", value.Kind) ||
            !IsExactEnumValue(element, "sector", value.Sector))
        {
            throw InvalidJournal();
        }

        RequireText(value.Narrative);
        var observations = element.GetProperty("observations");
        if (observations.ValueKind != JsonValueKind.Array || value.Observations is null)
        {
            throw InvalidJournal();
        }

        foreach (var observation in observations.EnumerateArray())
        {
            RequireProperties(observation, ["narrative"]);
        }

        if (value.Observations.Any(item => item is null || string.IsNullOrWhiteSpace(item.Narrative)))
        {
            throw InvalidJournal();
        }
    }

    private static void ValidateAssignedPayload(JsonElement payload, CaseAssignedPayload value) => RequireIdentifier(value.AccountableOwnerId);

    private static void ValidateRequestPayload(JsonElement payload, CaseActionRequestedPayload value)
    {
        if (!IsCanonicalGuid(payload, "requestId") ||
            !IsExactEnumValue(payload, "requestedOperation", value.RequestedOperation) || value.RequestId == Guid.Empty)
        {
            throw InvalidJournal();
        }

        RequireText(value.Reason);
        if (value.EvidenceEventIds is null || value.EvidenceEventIds.Any(id => !IsCanonicalGuid(id)))
        {
            throw InvalidJournal();
        }

        if (value.RequestedOperation == CaseAction.Reassign && value.ProposedOwnerId is null)
        {
            throw InvalidJournal();
        }

        if (value.ProposedOwnerId is not null)
        {
            RequireIdentifier(value.ProposedOwnerId);
        }
    }

    private static void ValidateReviewPayload(JsonElement payload, CaseActionRequestReviewedPayload value)
    {
        if (!IsCanonicalGuid(payload, "requestId") || !IsExactEnumValue(payload, "outcome", value.Outcome) || value.RequestId == Guid.Empty ||
            (value.Outcome == CaseActionReviewOutcome.Approved) != (value.LinkedAcceptedEventId is not null) ||
            (value.LinkedAcceptedEventId is not null && !IsCanonicalGuid(payload, "linkedAcceptedEventId")))
        {
            throw InvalidJournal();
        }

        RequireText(value.Reason);
    }

    private static void ValidateReasonPayload(JsonElement payload, CaseResolvedPayload value) => RequireText(value.Reason);

    private static void ValidateReopenedPayload(JsonElement payload, CaseReopenedPayload value)
    {
        RequireText(value.Reason);
        RequireIdentifier(value.AccountableOwnerId);
    }

    private static void ValidateObservationPayload(JsonElement payload, CaseObservationRecordedPayload value)
    {
        if (!IsCanonicalGuid(payload, "observationId") || value.ObservationId == Guid.Empty)
        {
            throw InvalidJournal();
        }

        RequireText(value.Narrative);
        RequireText(value.Source);
    }

    private static void ValidateFollowUpScheduledPayload(JsonElement payload, CaseFollowUpScheduledPayload value)
    {
        if (!IsCanonicalGuid(payload, "followUpId") || value.FollowUpId == Guid.Empty)
        {
            throw InvalidJournal();
        }

        RequireText(value.Purpose);
        RequireIdentifier(value.AssigneeActorId);
        if (value.DueAtUtc == default)
        {
            throw InvalidJournal();
        }
    }

    private static void ValidateFollowUpAcknowledgedPayload(JsonElement payload, CaseFollowUpAcknowledgedPayload value)
    {
        if (!IsCanonicalGuid(payload, "followUpId") || value.FollowUpId == Guid.Empty)
        {
            throw InvalidJournal();
        }
    }

    private static void ValidateFollowUpOutcomePayload(JsonElement payload, CaseFollowUpOutcomeRecordedPayload value)
    {
        if (!IsCanonicalGuid(payload, "followUpId") || value.FollowUpId == Guid.Empty ||
            !IsExactEnumValue(payload, "outcome", value.Outcome))
        {
            throw InvalidJournal();
        }

        RequireText(value.OutcomeNotes);
        RequireText(value.Reason);
        if (value.PreviousDueAtUtc == default)
        {
            throw InvalidJournal();
        }

        if ((value.Outcome == FollowUpOutcome.Rescheduled) != value.NewDueAtUtc.HasValue ||
            (value.NewDueAtUtc.HasValue && value.NewDueAtUtc.Value == default))
        {
            throw InvalidJournal();
        }
    }

    private static void ValidateActionOwnerChangedPayload(JsonElement payload, ActionOwnerChangedPayload value)
    {
        if (!IsExactEnumValue(payload, "actionKind", value.ActionKind) ||
            !IsCanonicalGuid(payload, "actionId") || value.ActionId == Guid.Empty ||
            !Enum.IsDefined(value.ActionKind))
            throw InvalidJournal();
        RequireIdentifier(value.PreviousOwnerId);
        RequireIdentifier(value.NewOwnerId);
        RequireText(value.Reason);
        if (StringComparer.Ordinal.Equals(value.PreviousOwnerId, value.NewOwnerId)) throw InvalidJournal();
    }

    private static void ValidateEscalationRaisedPayload(JsonElement payload, CaseEscalationRaisedPayload value)
    {
        if (!IsCanonicalGuid(payload, "escalationId") || value.EscalationId == Guid.Empty)
        {
            throw InvalidJournal();
        }

        RequireText(value.Reason);
        RequireIdentifier(value.RecipientRoute);
        if (value.EvidenceEventIds is null || value.EvidenceEventIds.Any(string.IsNullOrWhiteSpace))
        {
            throw InvalidJournal();
        }

        if (!payload.TryGetProperty("evidenceEventIds", out var evidence) ||
            evidence.ValueKind != JsonValueKind.Array ||
            evidence.GetArrayLength() != value.EvidenceEventIds.Count)
        {
            throw InvalidJournal();
        }
    }

    private static void ValidateEscalationAcknowledgedPayload(JsonElement payload, CaseEscalationAcknowledgedPayload value)
    {
        if (!IsCanonicalGuid(payload, "escalationId") || value.EscalationId == Guid.Empty)
        {
            throw InvalidJournal();
        }

        if (value.ActionSummary is not null && payload.GetProperty("actionSummary").ValueKind != JsonValueKind.String)
        {
            throw InvalidJournal();
        }

        RequireText(value.Reason);
    }

    private static void ValidateEscalationClosedPayload(JsonElement payload, CaseEscalationClosedPayload value)
    {
        if (!IsCanonicalGuid(payload, "escalationId") || value.EscalationId == Guid.Empty)
        {
            throw InvalidJournal();
        }

        RequireText(value.Resolution);
        RequireText(value.Reason);
    }

    private static void ValidateEscalationActionRecordedPayload(JsonElement payload, CaseEscalationActionRecordedPayload value)
    {
        if (!IsCanonicalGuid(payload, "escalationId") || value.EscalationId == Guid.Empty)
        {
            throw InvalidJournal();
        }

        RequireText(value.ActionSummary);
        RequireText(value.Reason);
    }

    private static ReportFacts ToDomainReport(CaseReportPayload report)
    {
        if (report is null)
        {
            throw InvalidJournal();
        }

        return new ReportFacts(report.Kind, report.Sector, report.Narrative,
            Array.AsReadOnly(report.Observations.Select(item => new ReportedObservation(item.Narrative)).ToArray()));
    }

    private static CaseReportEventData ToDomainReportV2(CaseReportV2Payload payload)
    {
        var context = new ReportContext(ToDomainFact(payload.Context.Occurrence), payload.Context.CapturedAtUtc,
            payload.Context.CapturedOffset, payload.Context.CapturedZoneId, payload.Context.CapturePrecision, payload.Context.Location);
        var evidence = new ReportEvidence(
            payload.Evidence.Narrative is null ? null : ToDomainText(payload.Evidence.Narrative),
            payload.Evidence.Observations.Select(ToDomainText),
            payload.Evidence.OriginalAudio is null ? null : new ReportEvidenceReference(
                payload.Evidence.OriginalAudio.Reference, ToDomainProvenance(payload.Evidence.OriginalAudio.Provenance)));
        var confirmations = new ReportConfirmations(payload.Confirmations.ReportReviewConfirmed,
            payload.Confirmations.OccurrenceCountryCode, payload.Confirmations.OccurrenceCountryCatalogueVersion,
            payload.Confirmations.OccurrenceCountryConfirmed, payload.Confirmations.ProcessingConfirmed,
            payload.Confirmations.ProcessingPolicyReference, payload.Confirmations.ProcessingPolicyVersion,
            payload.Confirmations.RecordingConfirmed, payload.Confirmations.RecordingPolicyReference,
            payload.Confirmations.RecordingPolicyVersion);

        return payload.ReportKind switch
        {
            "Human" when payload.Human is not null && payload.Animal is null => new CaseReportEventData.HumanV2(
                HumanReport.Create(payload.ReportId, context,
                    ToDomainFact(payload.Human.PseudonymousSubjectReference), ToDomainFact(payload.Human.AgeYears),
                    ToDomainFact(payload.Human.ReportedConcern), evidence), confirmations),
            "Animal" when payload.Animal is not null && payload.Human is null => new CaseReportEventData.AnimalV2(
                AnimalReport.Create(payload.ReportId, context, ToDomainSubjectScope(payload.Animal.SubjectScope),
                    ToDomainFact(payload.Animal.Species), ToDomainFact(payload.Animal.HoldingReference), evidence,
                    ToDomainCount(payload.Animal.AnimalsAtRisk), ToDomainCount(payload.Animal.AnimalsAffected),
                    ToDomainCount(payload.Animal.AnimalsDead)), confirmations),
            _ => throw InvalidJournal()
        };
    }

    private static ReportedFact<T> ToDomainFact<T>(ReportedFactV2Payload<T> fact)
    {
        var provenance = ToDomainProvenance(fact.Provenance);
        var value = fact.State switch
        {
            ReportedValueV2State.Unknown => ReportedFact<T>.Unknown(provenance),
            ReportedValueV2State.NotRecorded => ReportedFact<T>.NotRecorded(provenance),
            ReportedValueV2State.NotApplicable => ReportedFact<T>.NotApplicable(provenance),
            ReportedValueV2State.Absent => ReportedFact<T>.Absent(provenance),
            ReportedValueV2State.Known when fact.Value.HasValue => ReportedFact<T>.Known(
                fact.Value.Value.Deserialize<T>(JsonOptions) ?? throw InvalidJournal(), provenance),
            _ => throw InvalidJournal()
        };
        return value;
    }

    private static ReportProvenance ToDomainProvenance(ReportProvenanceV2Payload provenance) =>
        new(provenance.Source switch
        {
            ReportSourceV2Kind.ReporterNarrative => ReportSourceKind.ReporterNarrative,
            ReportSourceV2Kind.ReporterObservation => ReportSourceKind.ReporterObservation,
            ReportSourceV2Kind.OriginalAudio => ReportSourceKind.OriginalAudio,
            ReportSourceV2Kind.WorkerConfirmed => ReportSourceKind.WorkerConfirmed,
            _ => throw InvalidJournal()
        }, provenance.StableReference);

    private static AnimalSubjectScope ToDomainSubjectScope(AnimalSubjectV2Scope scope) => scope switch
    {
        AnimalSubjectV2Scope.Individual => AnimalSubjectScope.Individual,
        AnimalSubjectV2Scope.Herd => AnimalSubjectScope.Herd,
        AnimalSubjectV2Scope.Flock => AnimalSubjectScope.Flock,
        AnimalSubjectV2Scope.OtherGroup => AnimalSubjectScope.OtherGroup,
        _ => throw InvalidJournal()
    };

    private static ReportedText ToDomainText(ReportedTextV2Payload text) =>
        new(text.Text, ToDomainProvenance(text.Provenance));

    private static AnimalCount ToDomainCount(AnimalCountV2Payload count)
    {
        var state = count.State switch
        {
            ReportedValueV2State.Unknown => ReportedValueState.Unknown,
            ReportedValueV2State.NotRecorded => ReportedValueState.NotRecorded,
            ReportedValueV2State.NotApplicable => ReportedValueState.NotApplicable,
            ReportedValueV2State.Absent => ReportedValueState.Absent,
            ReportedValueV2State.Known => ReportedValueState.Known,
            _ => throw InvalidJournal()
        };
        var value = state == ReportedValueState.Known
            ? ReportedValue<int>.Known(count.Value!.Value)
            : state switch
            {
                ReportedValueState.Unknown => ReportedValue<int>.Unknown,
                ReportedValueState.NotRecorded => ReportedValue<int>.NotRecorded,
                ReportedValueState.NotApplicable => ReportedValue<int>.NotApplicable,
                ReportedValueState.Absent => ReportedValue<int>.Absent,
                _ => throw InvalidJournal()
            };
        return new AnimalCount(value, ToDomainProvenance(count.Provenance), count.PopulationReference,
            count.ObservationPeriod is null ? null : ToDomainFact(count.ObservationPeriod));
    }

    private static void ValidateRowAgreement(CaseEventEntity row, CaseEvent @event, int schemaVersion)
    {
        if (row.EventId == Guid.Empty || row.CaseId == Guid.Empty || row.CommandId == Guid.Empty ||
            row.AggregateVersion <= 0 || row.ReportRevision <= 0 || row.SchemaVersion != schemaVersion ||
            string.IsNullOrWhiteSpace(row.EventType) || !IsCanonicalIdentifier(row.ActorId) ||
            row.PayloadId == Guid.Empty ||
            @event.EventId != row.EventId || @event.CaseId != row.CaseId ||
            @event.CommandId != row.CommandId || !string.Equals(@event.ActorId, row.ActorId, StringComparison.Ordinal) ||
            @event.AggregateVersion != row.AggregateVersion || @event.ReportRevision != row.ReportRevision ||
            !string.Equals(@event.GetType().Name, row.EventType, StringComparison.Ordinal) ||
            !SamePostgresTimestamp(@event.RecordedAtUtc, row.RecordedAtUtc) ||
            !SamePostgresTimestamp(@event.OccurredAtUtc, row.OccurredAtUtc))
        {
            throw InvalidJournal();
        }
    }

    internal static bool SamePostgresTimestamp(DateTimeOffset? source, DateTimeOffset? stored)
    {
        if (source is null || stored is null)
        {
            return source is null && stored is null;
        }

        var sourceUtc = source.Value.ToUniversalTime();
        var storedUtc = stored.Value.ToUniversalTime();
        return TruncateToMicroseconds(sourceUtc).Ticks == TruncateToMicroseconds(storedUtc).Ticks;
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);

    private static void RequireProperties(JsonElement element, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidJournal();
        }

        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Count || expected.Any(name => !names.Contains(name, StringComparer.Ordinal)))
        {
            throw InvalidJournal();
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw InvalidJournal();
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static bool IsCanonicalGuid(string? value) =>
        value is not null && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(value, parsed.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static bool IsCanonicalGuid(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String &&
        IsCanonicalGuid(value.GetString());

    private static void RequireCanonicalGuid(JsonElement element, string propertyName)
    {
        if (!IsCanonicalGuid(element, propertyName))
        {
            throw InvalidJournal();
        }
    }

    private static bool IsExactEnumValue<TEnum>(JsonElement element, string propertyName, TEnum value)
        where TEnum : struct, Enum =>
        element.TryGetProperty(propertyName, out var token) && token.ValueKind == JsonValueKind.String &&
        string.Equals(token.GetString(), value.ToString(), StringComparison.Ordinal) && Enum.IsDefined(value);

    private static void RequireText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidJournal();
        }
    }

    private static void RequireIdentifier(string? value)
    {
        if (!IsCanonicalIdentifier(value))
        {
            throw InvalidJournal();
        }
    }

    private static bool IsCanonicalIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static InvalidDataException InvalidJournal() =>
        new("The persisted case event journal is invalid or unsupported.");
}
