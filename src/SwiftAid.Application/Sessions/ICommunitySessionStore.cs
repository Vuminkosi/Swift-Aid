using SwiftAid.Domain.Sessions;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Application.Sessions;

/// <summary>Accepted lifecycle action recorded with a session revision.</summary>
public enum SessionRevisionAction
{
    Created,
    Reviewed,
    Scheduled,
    Updated,
    Cancelled,
    Completed,
    Rescheduled
}

/// <summary>
/// How the store should synchronize reminder occurrences after a session change.
/// Dispatch/execution of reminders is explicitly OUT of scope (deferred to the
/// P6.3 durable-reminder lane): rows in <c>Sent</c> status are always left untouched.
/// </summary>
public enum SessionReminderSync
{
    /// <summary>No reminder change (create, review, read paths).</summary>
    None,

    /// <summary>
    /// Delete non-sent (Pending/Suppressed) rows and insert the supplied occurrences
    /// as Pending. Used after schedule and update, computed via
    /// <see cref="CommunitySession.ComputeReminderOccurrences"/>.
    /// </summary>
    Recompute,

    /// <summary>Suppress pending work and mark in-progress deliveries outcome unknown after cancellation or completion. Sent rows stay Sent.</summary>
    SuppressPending,
}

/// <summary>
/// Persistence port for community sessions. Implementations keep PostgreSQL
/// authoritative; Qdrant is never a case database and plays no role here.
/// Staff authorization is enforced by the API layer (staff authentication); this
/// port trusts the actorId passed by the service.
/// </summary>
public interface ICommunitySessionStore
{
    Task<CommunitySession?> LoadAsync(Guid sessionId, CancellationToken ct);

    Task InsertAsync(
        CommunitySession session,
        string actorId,
        string reason,
        DateTimeOffset recordedAtUtc,
        SessionRevisionAction action,
        CancellationToken ct);

    Task UpdateAsync(
        CommunitySession session,
        long expectedVersion,
        string actorId,
        string reason,
        DateTimeOffset recordedAtUtc,
        SessionReminderSync reminderSync,
        IReadOnlyList<SessionReminderOccurrence>? occurrences,
        SessionRevisionAction action,
        DateTimeOffset? previousStartUtc,
        CancellationToken ct);

    Task<IReadOnlyList<CommunitySession>> ListPublicAsync(int take, CancellationToken ct);

    /// <summary>Filters public sessions before applying the bounded continuation and limit.</summary>
    Task<IReadOnlyList<CommunitySession>> QueryPublicAsync(PublicSessionQuery query, int take, CancellationToken ct);

    Task<int> CountRevisionsAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Reads staff-only revision metadata; callers must authorize the session first.</summary>
    Task<IReadOnlyList<SessionHistoryEntry>> ReadHistoryAsync(Guid sessionId, long? beforeVersion, int take, CancellationToken ct);

    Task<int> CountPendingRemindersAsync(Guid sessionId, CancellationToken ct);

    Task<int> CountSuppressedRemindersAsync(Guid sessionId, CancellationToken ct);
}
