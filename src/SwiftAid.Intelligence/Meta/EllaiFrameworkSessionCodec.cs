using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Serializes framework state while binding it to its application session and owner.</summary>
public static class EllaiFrameworkSessionCodec
{
    public const int MaximumBytes = 1_048_576;
    private const int Version = 1;

    /// <summary>A changed scope starts a new framework session; owner or session mismatches are invalid.</summary>
    public static bool IsCurrentScope(string json, string sessionId, string ownerActorId, string scopeEpoch)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("Framework session exceeds the permitted size.");
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(json); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid framework session.", ex); }
        if (envelope is null || envelope.Version != Version ||
            !string.Equals(envelope.SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(envelope.OwnerActorId, ownerActorId, StringComparison.Ordinal) ||
            envelope.State.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Framework session does not match its owner or session.");
        return string.Equals(envelope.ScopeEpoch, scopeEpoch, StringComparison.Ordinal);
    }

    public static async Task<string> SerializeAsync(AIAgent agent, AgentSession session,
        string sessionId, string ownerActorId, string scopeEpoch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeEpoch);
        var state = await agent.SerializeSessionAsync(session, cancellationToken: ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(new Envelope(Version, sessionId, ownerActorId, scopeEpoch, state));
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("Framework session exceeds the permitted size.");
        return json;
    }

    /// <summary>Restore opaque framework state, replacing its history with the currently authorized projection.</summary>
    public static async Task<AgentSession> RestoreAsync(AIAgent agent, string json, string sessionId,
        string ownerActorId, string scopeEpoch, IReadOnlyList<ChatMessage> authorizedHistory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeEpoch);
        ArgumentNullException.ThrowIfNull(authorizedHistory);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("Framework session exceeds the permitted size.");
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(json); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid framework session.", ex); }
        if (envelope is null || envelope.Version != Version ||
            !string.Equals(envelope.SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(envelope.OwnerActorId, ownerActorId, StringComparison.Ordinal) ||
            !string.Equals(envelope.ScopeEpoch, scopeEpoch, StringComparison.Ordinal) ||
            envelope.State.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Framework session does not match its owner or session.");
        var restored = await agent.DeserializeSessionAsync(envelope.State, cancellationToken: ct).ConfigureAwait(false);
        restored.SetInMemoryChatHistory(authorizedHistory.ToList());
        return restored;
    }

    private sealed record Envelope(int Version, string SessionId, string OwnerActorId, string ScopeEpoch, JsonElement State);
}
