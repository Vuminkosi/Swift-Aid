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
            CaseReported reported => Serialize(reported, CaseEventType.CaseReported,
                new CaseReportedPayload(reported.WorkspaceId, MapReport(reported.Report))),
            CaseAssigned assignment => Serialize(assignment, CaseEventType.CaseAssigned,
                new CaseAssignedPayload(assignment.AccountableOwnerId)),
            CaseReportCorrected correction => Serialize(correction, CaseEventType.CaseReportCorrected,
                new CaseReportCorrectedPayload(MapReport(correction.CorrectedReport), correction.Reason, correction.PreviousReportRevision)),
            CaseActionRequested request => Serialize(request, CaseEventType.CaseActionRequested,
                MapRequest(request)),
            CaseActionRequestReviewed review => Serialize(review, CaseEventType.CaseActionRequestReviewed,
                MapReview(review)),
            CaseResolved resolution => Serialize(resolution, CaseEventType.CaseResolved,
                new CaseResolvedPayload(resolution.Reason)),
            CaseReopened reopening => Serialize(reopening, CaseEventType.CaseReopened,
                new CaseReopenedPayload(reopening.Reason, reopening.AccountableOwnerId)),
            _ => throw new NotSupportedException($"Event type {@event.GetType().Name} is not supported by the case-event v1 contract.")
        };
    }

    private static byte[] Serialize<TPayload>(CaseEvent @event, CaseEventType eventType, TPayload payload)
        where TPayload : CaseEventPayload
    {
        var envelope = new CaseEventEnvelope<TPayload>(
            SchemaVersion: 1,
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

    private static CaseActionRequestedPayload MapRequest(CaseActionRequested request)
    {
        if (!Enum.IsDefined(request.RequestedOperation))
        {
            throw new InvalidDataException("The requested operation is outside the case-event v1 contract.");
        }

        return new CaseActionRequestedPayload(request.RequestId, request.RequestedOperation, request.Reason,
            Array.AsReadOnly(request.EvidenceEventIds.ToArray()), request.ProposedOwnerId);
    }

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
