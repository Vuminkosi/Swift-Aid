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
    string? Outcome);

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
            input.Outcome);
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
            intent.Outcome), JsonOptions);
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
                envelope.Outcome);
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
        string? Outcome);
}
