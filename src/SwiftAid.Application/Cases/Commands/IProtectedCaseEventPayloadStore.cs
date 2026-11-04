namespace SwiftAid.Application.Cases.Commands;

/// <summary>Stages and opens case-event payloads protected for one immutable event identity.</summary>
public interface IProtectedCaseEventPayloadStore
{
    /// <summary>
    /// Protects serialized bytes and adds only the ciphertext record to the current transaction's
    /// change tracker. This method never saves the transaction.
    /// </summary>
    ValueTask<ProtectedCaseEventPayloadReference> StageProtectedAsync(
        Guid caseId,
        Guid payloadId,
        string eventType,
        int schemaVersion,
        ReadOnlyMemory<byte> serializedBytes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a ciphertext record using the exact purpose metadata read from its linked case event
    /// and payload rows. The implementation validates the persisted payload bytes, purpose version,
    /// and matching case/event/schema linkage. The journal reader remains responsible for checking
    /// the event ID, command, actor, aggregate/report versions, timestamps, and strict payload schema.
    /// </summary>
    ValueTask<byte[]> UnprotectAsync(
        Guid caseId,
        Guid payloadId,
        string eventType,
        int schemaVersion,
        string protectionPurposeVersion,
        ReadOnlyMemory<byte> protectedBytes,
        CancellationToken cancellationToken);
}

/// <summary>Opaque reference metadata for a payload protected with a versioned purpose.</summary>
public readonly record struct ProtectedCaseEventPayloadReference(
    Guid PayloadId,
    string ProtectionPurposeVersion);

/// <summary>A sanitized failure for a protected event-payload operation.</summary>
public sealed class ProtectedCaseEventPayloadException : Exception
{
    public ProtectedCaseEventPayloadException()
        : base("The protected case event payload operation could not be completed.")
    {
    }
}
