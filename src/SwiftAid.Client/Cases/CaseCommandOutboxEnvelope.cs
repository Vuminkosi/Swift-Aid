using System.Text.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

/// <summary>
/// Durable intent bytes for one staff case command held in the local command outbox.
/// The payload carries routing fields only; authorization always derives from the
/// server-authenticated principal at send time. Serialized deterministically so a
/// frozen retry always sends the same bytes and hash.
/// </summary>
public sealed record CaseCommandIntent(
    string CommandType,
    string? Action,
    string? Reason,
    string? ProposedOwnerId,
    Guid? RequestId,
    string? Outcome,
    Guid? ObservationId = null,
    string? Narrative = null,
    string? Source = null,
    Guid? FollowUpId = null,
    string? Purpose = null,
    string? AssigneeActorId = null,
    DateTimeOffset? DueAtUtc = null,
    string? OutcomeName = null,
    string? OutcomeNotes = null,
    DateTimeOffset? NewDueAtUtc = null,
    Guid? EscalationId = null,
    string? RecipientRoute = null,
    string? Resolution = null,
    string? ActionSummary = null,
    IReadOnlyList<string>? EvidenceEventIds = null);

/// <summary>Version-one JSON codec for <see cref="CaseCommandIntent"/> outbox payloads.</summary>
public static class CaseCommandIntentCodec
{
    private const int IntentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static CaseCommandIntent FromInput(CaseCommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new CaseCommandIntent(
            input.CommandType,
            input.Action,
            input.Reason,
            input.ProposedOwnerId,
            input.RequestId,
            input.Outcome,
            input.ObservationId,
            input.Narrative,
            input.Source,
            input.FollowUpId,
            input.Purpose,
            input.AssigneeActorId,
            input.DueAtUtc,
            input.OutcomeName,
            input.OutcomeNotes,
            input.NewDueAtUtc,
            input.EscalationId,
            input.RecipientRoute,
            input.Resolution,
            input.ActionSummary,
            input.EvidenceEventIds);
    }

    public static byte[] Encode(CaseCommandIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (string.IsNullOrWhiteSpace(intent.CommandType))
        {
            throw new ArgumentException("A case command type is required.", nameof(intent));
        }

        return JsonSerializer.SerializeToUtf8Bytes(new IntentEnvelope(
            IntentSchemaVersion,
            intent.CommandType,
            intent.Action,
            intent.Reason,
            intent.ProposedOwnerId,
            intent.RequestId,
            intent.Outcome,
            intent.ObservationId,
            intent.Narrative,
            intent.Source,
            intent.FollowUpId,
            intent.Purpose,
            intent.AssigneeActorId,
            intent.DueAtUtc,
            intent.OutcomeName,
            intent.OutcomeNotes,
            intent.NewDueAtUtc,
            intent.EscalationId,
            intent.RecipientRoute,
            intent.Resolution,
            intent.ActionSummary,
            intent.EvidenceEventIds), JsonOptions);
    }

    public static CaseCommandIntent Decode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            var envelope = JsonSerializer.Deserialize<IntentEnvelope>(payload, JsonOptions);
            if (envelope is null || envelope.SchemaVersion != IntentSchemaVersion ||
                string.IsNullOrWhiteSpace(envelope.CommandType))
            {
                throw new InvalidOperationException("The stored case-command intent is not a supported version-one payload.");
            }

            return new CaseCommandIntent(
                envelope.CommandType,
                envelope.Action,
                envelope.Reason,
                envelope.ProposedOwnerId,
                envelope.RequestId,
                envelope.Outcome,
                envelope.ObservationId,
                envelope.Narrative,
                envelope.Source,
                envelope.FollowUpId,
                envelope.Purpose,
                envelope.AssigneeActorId,
                envelope.DueAtUtc,
                envelope.OutcomeName,
                envelope.OutcomeNotes,
                envelope.NewDueAtUtc,
                envelope.EscalationId,
                envelope.RecipientRoute,
                envelope.Resolution,
                envelope.ActionSummary,
                envelope.EvidenceEventIds);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The stored case-command intent is not valid.", ex);
        }
    }

    private sealed record IntentEnvelope(
        int SchemaVersion,
        string? CommandType,
        string? Action,
        string? Reason,
        string? ProposedOwnerId,
        Guid? RequestId,
        string? Outcome,
        Guid? ObservationId,
        string? Narrative,
        string? Source,
        Guid? FollowUpId,
        string? Purpose,
        string? AssigneeActorId,
        DateTimeOffset? DueAtUtc,
        string? OutcomeName,
        string? OutcomeNotes,
        DateTimeOffset? NewDueAtUtc,
        Guid? EscalationId,
        string? RecipientRoute,
        string? Resolution,
        string? ActionSummary,
        IReadOnlyList<string>? EvidenceEventIds);
}
