using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Events;
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

            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("eventType").ValueKind != JsonValueKind.String)
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

            var result = eventType switch
            {
                nameof(CaseEventType.CaseReported) => Read<CaseReportedPayload>(serialized,
                    ["workspaceId", "report"], ValidateReportPayload),
                nameof(CaseEventType.CaseAssigned) => Read<CaseAssignedPayload>(serialized,
                    ["accountableOwnerId"], ValidateAssignedPayload),
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
                _ => throw InvalidJournal()
            };

            ValidateRowAgreement(row, result);
            return result;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
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
            _ => throw InvalidJournal()
        };
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

    private static ReportFacts ToDomainReport(CaseReportPayload report)
    {
        if (report is null)
        {
            throw InvalidJournal();
        }

        return new ReportFacts(report.Kind, report.Sector, report.Narrative,
            Array.AsReadOnly(report.Observations.Select(item => new ReportedObservation(item.Narrative)).ToArray()));
    }

    private static void ValidateRowAgreement(CaseEventEntity row, CaseEvent @event)
    {
        if (row.EventId == Guid.Empty || row.CaseId == Guid.Empty || row.CommandId == Guid.Empty ||
            row.AggregateVersion <= 0 || row.ReportRevision <= 0 || row.SchemaVersion != 1 ||
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
