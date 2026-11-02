using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases.Commands;

/// <summary>Creates stable protocol-v1 hashes and immutable command snapshots for receipts.</summary>
public static class CanonicalCaseCommandHasher
{
    public const string ProtocolId = "swiftaid.case-command-receipt";
    public const int CurrentReceiptProtocolVersion = 1;
    public const int CurrentWireCommandSchemaVersion = 1;

    public static void EnsureSupportedReceiptProtocolVersion(int receiptProtocolVersion)
    {
        if (receiptProtocolVersion != CurrentReceiptProtocolVersion)
        {
            throw new NotSupportedException("The stored canonical receipt schema is not supported.");
        }
    }

    public static CanonicalCaseCommandSnapshot CreateSnapshot(
        ActorContext actor,
        Guid caseId,
        int receiptProtocolVersion,
        int wireCommandSchemaVersion,
        CaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);

        if (receiptProtocolVersion != CurrentReceiptProtocolVersion)
        {
            throw new NotSupportedException("The canonical command hash protocol is not supported.");
        }

        if (wireCommandSchemaVersion != CurrentWireCommandSchemaVersion)
        {
            throw new NotSupportedException("The command schema version is not supported.");
        }

        EnsureSupportedCommand(command);
        RequireIdentifier(actor.ActorId, nameof(actor));
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("A case identifier is required.", nameof(caseId));
        }

        if (command.CommandId == Guid.Empty)
        {
            throw new ArgumentException("A command identifier is required.", nameof(command));
        }

        if (command.ExpectedVersion < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "A non-creation case command requires an existing aggregate version.");
        }

        var (snapshotCommand, commandType, reason, payloadWriter) = SnapshotCommand(command);
        var canonicalHash = HashCanonicalRequest(
            actor.ActorId,
            caseId,
            receiptProtocolVersion,
            wireCommandSchemaVersion,
            commandType,
            snapshotCommand,
            reason,
            payloadWriter);

        return new CanonicalCaseCommandSnapshot(
            receiptProtocolVersion,
            wireCommandSchemaVersion,
            actor.ActorId,
            caseId,
            commandType,
            snapshotCommand,
            canonicalHash);
    }

    private static void EnsureSupportedCommand(CaseCommand command)
    {
        if (command is AcceptCaseCommand)
        {
            throw new NotSupportedException("Case creation is not supported by the non-creation command hash protocol.");
        }

        if (command is not (CorrectReportCommand or RequestCaseActionCommand or ReviewCaseActionRequestCommand or ResolveCaseCommand or ReopenCaseCommand
            or RecordObservationCommand or ScheduleFollowUpCommand or RecordFollowUpOutcomeCommand
            or RaiseEscalationCommand or AcknowledgeEscalationCommand or CloseEscalationCommand or AcknowledgeAssignmentCommand))
        {
            throw new NotSupportedException($"Command type {command.GetType().Name} is not supported by canonical hash protocol v1.");
        }
    }

    private static (CaseCommand Command, string CommandType, string Reason, Action<Utf8JsonWriter> WritePayload)
        SnapshotCommand(CaseCommand command)
    {
        switch (command)
        {
            case AcknowledgeAssignmentCommand assignment:
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(assignment.AssignmentVersion);
                var assignmentSnapshot = assignment with { OccurredAtUtc = Normalize(assignment.OccurredAtUtc) };
                return (assignmentSnapshot, "AcknowledgeAssignment", "Assignment acknowledged", writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("assignmentVersion", assignmentSnapshot.AssignmentVersion);
                    writer.WriteEndObject();
                });

            case CorrectReportCommand correction:
                RequireText(correction.CorrectedNarrative, nameof(correction.CorrectedNarrative));
                RequireText(correction.Reason, nameof(correction.Reason));
                var corrected = correction with { OccurredAtUtc = Normalize(correction.OccurredAtUtc) };
                return (corrected, "CorrectReport", corrected.Reason, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("correctedNarrative", corrected.CorrectedNarrative);
                    writer.WriteEndObject();
                });

            case RequestCaseActionCommand request:
                if (!Enum.IsDefined(request.Action))
                {
                    throw new ArgumentOutOfRangeException(nameof(command), "The case action is unknown.");
                }

                RequireText(request.Reason, nameof(request.Reason));
                RequireIdentifier(request.RequestId, nameof(request.RequestId));
                ArgumentNullException.ThrowIfNull(request.EvidenceEventIds);
                var evidence = request.EvidenceEventIds.ToArray();
                foreach (var eventId in evidence)
                {
                    RequireIdentifier(eventId, nameof(request.EvidenceEventIds));
                }

                if (request.ProposedOwnerId is not null)
                {
                    RequireIdentifier(request.ProposedOwnerId, nameof(request.ProposedOwnerId));
                }

                if (request.Action == CaseAction.Reassign && request.ProposedOwnerId is null)
                {
                    throw new ArgumentException("A reassignment request requires a proposed owner identifier.", nameof(request));
                }

                var requestSnapshot = request with
                {
                    OccurredAtUtc = Normalize(request.OccurredAtUtc),
                    EvidenceEventIds = Array.AsReadOnly(evidence)
                };
                return (requestSnapshot, "RequestCaseAction", requestSnapshot.Reason, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("requestId", requestSnapshot.RequestId.ToString("D"));
                    writer.WriteString("action", requestSnapshot.Action.ToString());
                    writer.WriteStartArray("evidenceEventIds");
                    foreach (var eventId in requestSnapshot.EvidenceEventIds)
                    {
                        writer.WriteStringValue(eventId);
                    }

                    writer.WriteEndArray();
                    WriteNullableString(writer, "proposedOwnerId", requestSnapshot.ProposedOwnerId);
                    writer.WriteEndObject();
                });

            case ReviewCaseActionRequestCommand review:
                if (!Enum.IsDefined(review.Outcome))
                {
                    throw new ArgumentOutOfRangeException(nameof(command), "The review outcome is unknown.");
                }

                RequireIdentifier(review.RequestId, nameof(review.RequestId));
                RequireText(review.Reason, nameof(review.Reason));
                if (review.AcceptedOwnerId is not null)
                {
                    RequireIdentifier(review.AcceptedOwnerId, nameof(review.AcceptedOwnerId));
                }

                var reviewSnapshot = review with { OccurredAtUtc = Normalize(review.OccurredAtUtc) };
                return (reviewSnapshot, "ReviewCaseActionRequest", reviewSnapshot.Reason, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("requestId", reviewSnapshot.RequestId.ToString("D"));
                    writer.WriteString("outcome", reviewSnapshot.Outcome.ToString());
                    WriteNullableString(writer, "acceptedOwnerId", reviewSnapshot.AcceptedOwnerId);
                    writer.WriteEndObject();
                });

            case ResolveCaseCommand resolve:
                RequireText(resolve.Reason, nameof(resolve.Reason));
                var resolveSnapshot = resolve with { OccurredAtUtc = Normalize(resolve.OccurredAtUtc) };
                return (resolveSnapshot, "ResolveCase", resolveSnapshot.Reason, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                });

            case ReopenCaseCommand reopen:
                RequireText(reopen.Reason, nameof(reopen.Reason));
                RequireIdentifier(reopen.AccountableOwnerId, nameof(reopen.AccountableOwnerId));
                var reopenSnapshot = reopen with { OccurredAtUtc = Normalize(reopen.OccurredAtUtc) };
                return (reopenSnapshot, "ReopenCase", reopenSnapshot.Reason, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("accountableOwnerId", reopenSnapshot.AccountableOwnerId);
                    writer.WriteEndObject();
                });

            case AcceptCaseCommand:
                throw new NotSupportedException("Case creation is not supported by the non-creation command hash protocol.");

            case RecordObservationCommand observation:
                RequireIdentifier(observation.ObservationId, nameof(observation.ObservationId));
                RequireText(observation.Narrative, nameof(observation.Narrative));
                RequireText(observation.Source, nameof(observation.Source));
                RequireText(observation.Reason, nameof(observation.Reason));
                var observationSnapshot = observation with { OccurredAtUtc = Normalize(observation.OccurredAtUtc) };
                return (observationSnapshot, "RecordObservation", observationSnapshot.Reason, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("observationId", observationSnapshot.ObservationId.ToString("D"));
                    writer.WriteString("narrative", observationSnapshot.Narrative);
                    writer.WriteString("source", observationSnapshot.Source);
                    writer.WriteEndObject();
                });

            case ScheduleFollowUpCommand schedule:
                RequireIdentifier(schedule.FollowUpId, nameof(schedule.FollowUpId));
                RequireText(schedule.Purpose, nameof(schedule.Purpose));
                RequireIdentifier(schedule.AssigneeActorId, nameof(schedule.AssigneeActorId));
                RequireText(schedule.Reason, nameof(schedule.Reason));
                var scheduleSnapshot = schedule with { OccurredAtUtc = Normalize(schedule.OccurredAtUtc) };
                return (scheduleSnapshot, "ScheduleFollowUp", scheduleSnapshot.Reason, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("followUpId", scheduleSnapshot.FollowUpId.ToString("D"));
                    writer.WriteString("purpose", scheduleSnapshot.Purpose);
                    writer.WriteString("assigneeActorId", scheduleSnapshot.AssigneeActorId);
                    writer.WriteString("dueAtUtc", scheduleSnapshot.DueAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    writer.WriteEndObject();
                });

            case RecordFollowUpOutcomeCommand outcome:
                if (!Enum.IsDefined(outcome.Outcome))
                {
                    throw new ArgumentOutOfRangeException(nameof(outcome), "The follow-up outcome is unknown.");
                }

                RequireIdentifier(outcome.FollowUpId, nameof(outcome.FollowUpId));
                RequireText(outcome.OutcomeNotes, nameof(outcome.OutcomeNotes));
                RequireText(outcome.Reason, nameof(outcome.Reason));
                var outcomeSnapshot = outcome with { OccurredAtUtc = Normalize(outcome.OccurredAtUtc) };
                return (outcomeSnapshot, "RecordFollowUpOutcome", outcomeSnapshot.Reason, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("followUpId", outcomeSnapshot.FollowUpId.ToString("D"));
                    writer.WriteString("outcome", outcomeSnapshot.Outcome.ToString());
                    writer.WriteString("outcomeNotes", outcomeSnapshot.OutcomeNotes);
                    if (outcomeSnapshot.NewDueAtUtc is null)
                    {
                        writer.WriteNull("newDueAtUtc");
                    }
                    else
                    {
                        writer.WriteString("newDueAtUtc", outcomeSnapshot.NewDueAtUtc.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    }

                    writer.WriteEndObject();
                });

            case RaiseEscalationCommand escalation:
                RequireIdentifier(escalation.EscalationId, nameof(escalation.EscalationId));
                RequireText(escalation.Reason, nameof(escalation.Reason));
                RequireIdentifier(escalation.RecipientRoute, nameof(escalation.RecipientRoute));
                ArgumentNullException.ThrowIfNull(escalation.EvidenceEventIds);
                var escalationEvidence = escalation.EvidenceEventIds.ToArray();
                foreach (var eventId in escalationEvidence)
                {
                    RequireIdentifier(eventId, nameof(escalation.EvidenceEventIds));
                }

                var escalationSnapshot = escalation with
                {
                    OccurredAtUtc = Normalize(escalation.OccurredAtUtc),
                    EvidenceEventIds = Array.AsReadOnly(escalationEvidence)
                };
                return (escalationSnapshot, "RaiseEscalation", escalationSnapshot.Reason, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("escalationId", escalationSnapshot.EscalationId.ToString("D"));
                    writer.WriteStartArray("evidenceEventIds");
                    foreach (var eventId in escalationSnapshot.EvidenceEventIds)
                    {
                        writer.WriteStringValue(eventId);
                    }

                    writer.WriteEndArray();
                    writer.WriteString("recipientRoute", escalationSnapshot.RecipientRoute);
                    writer.WriteEndObject();
                });

            case AcknowledgeEscalationCommand acknowledge:
                RequireIdentifier(acknowledge.EscalationId, nameof(acknowledge.EscalationId));
                RequireText(acknowledge.Reason, nameof(acknowledge.Reason));
                var acknowledgeSnapshot = acknowledge with { OccurredAtUtc = Normalize(acknowledge.OccurredAtUtc) };
                return (acknowledgeSnapshot, "AcknowledgeEscalation", acknowledgeSnapshot.Reason, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("escalationId", acknowledgeSnapshot.EscalationId.ToString("D"));
                    WriteNullableString(writer, "actionSummary", acknowledgeSnapshot.ActionSummary);
                    writer.WriteEndObject();
                });

            case CloseEscalationCommand close:
                RequireIdentifier(close.EscalationId, nameof(close.EscalationId));
                RequireText(close.Resolution, nameof(close.Resolution));
                RequireText(close.Reason, nameof(close.Reason));
                var closeSnapshot = close with { OccurredAtUtc = Normalize(close.OccurredAtUtc) };
                return (closeSnapshot, "CloseEscalation", closeSnapshot.Reason, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("escalationId", closeSnapshot.EscalationId.ToString("D"));
                    writer.WriteString("resolution", closeSnapshot.Resolution);
                    writer.WriteEndObject();
                });

            default:
                throw new NotSupportedException($"Command type {command.GetType().Name} is not supported by canonical hash protocol v1.");
        }
    }

    private static byte[] HashCanonicalRequest(
        string actorId,
        Guid caseId,
        int receiptProtocolVersion,
        int wireCommandSchemaVersion,
        string commandType,
        CaseCommand command,
        string reason,
        Action<Utf8JsonWriter> writePayload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        // This is the private receipt-hash protocol, not the external command wire envelope.
        // Its field names and order are pinned independently by the normative golden fixture.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("protocolId", ProtocolId);
            writer.WriteNumber("receiptProtocolVersion", receiptProtocolVersion);
            writer.WriteNumber("wireCommandSchemaVersion", wireCommandSchemaVersion);
            writer.WriteString("actorId", actorId);
            writer.WriteString("caseId", caseId.ToString("D"));
            writer.WriteString("commandId", command.CommandId.ToString("D"));
            writer.WriteString("commandType", commandType);
            writer.WriteNumber("expectedVersion", command.ExpectedVersion);
            WriteNullableTimestamp(writer, "occurredAtUtc", command.OccurredAtUtc);
            writer.WriteString("reason", reason);
            writer.WritePropertyName("payload");
            writePayload(writer);
            writer.WriteEndObject();
        }

        return SHA256.HashData(buffer.WrittenSpan);
    }

    private static void WriteNullableTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, value);
    }

    private static DateTimeOffset? Normalize(DateTimeOffset? value) => value?.ToUniversalTime();

    private static void RequireIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", parameterName);
        }
    }

    private static void RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A non-empty canonical identifier is required.", parameterName);
        }
    }

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }
}

/// <summary>An immutable typed command snapshot and opaque SHA-256 receipt hash.</summary>
public sealed class CanonicalCaseCommandSnapshot
{
    private readonly byte[] _payloadHash;

    internal CanonicalCaseCommandSnapshot(
        int receiptProtocolVersion,
        int wireCommandSchemaVersion,
        string actorId,
        Guid caseId,
        string commandType,
        CaseCommand command,
        byte[] payloadHash)
    {
        ProtocolId = CanonicalCaseCommandHasher.ProtocolId;
        ReceiptProtocolVersion = receiptProtocolVersion;
        WireCommandSchemaVersion = wireCommandSchemaVersion;
        ActorId = actorId;
        CaseId = caseId;
        CommandId = command.CommandId;
        CommandType = commandType;
        Command = command;
        _payloadHash = payloadHash.ToArray();
    }

    public string ProtocolId { get; }

    public int ReceiptProtocolVersion { get; }

    /// <summary>The external typed command schema supported by receipt protocol v1.</summary>
    public int WireCommandSchemaVersion { get; }

    public string ActorId { get; }

    public Guid CaseId { get; }

    public Guid CommandId { get; }

    public string CommandType { get; }

    /// <summary>The cloned typed command that must be executed after hashing.</summary>
    public CaseCommand Command { get; }

    /// <summary>Returns a defensive copy of the canonical SHA-256 payload hash.</summary>
    public byte[] CanonicalPayloadHash => _payloadHash.ToArray();

    public override string ToString() => $"CanonicalCaseCommandSnapshot(v{ReceiptProtocolVersion}, {CommandId:D})";
}
