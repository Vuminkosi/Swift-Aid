namespace SwiftAid.Contracts.Sessions;

/// <summary>An accepted, staff-only session revision. Missing historical metadata remains unknown.</summary>
public sealed record SessionHistoryEntry(
    long Version,
    string ActorId,
    string Reason,
    DateTimeOffset RecordedAtUtc,
    string? Action,
    string? Status,
    DateTimeOffset? StartUtc,
    DateTimeOffset? PreviousStartUtc);

/// <summary>Descending version page; the cursor is a position, not an authorization grant.</summary>
public sealed record SessionHistoryPage(
    Guid SessionId,
    IReadOnlyList<SessionHistoryEntry> Items,
    long? BeforeVersion);
