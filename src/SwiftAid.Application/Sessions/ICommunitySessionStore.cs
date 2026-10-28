using SwiftAid.Domain.Sessions;

namespace SwiftAid.Application.Sessions;

/// <summary>
/// How the store should synchronize reminder occurrences after a session change.
/// Dispatch/execution of reminders is explicitly OUT of scope (deferred to the
/// P6.3 durable-reminder lane): rows in <c>Sent</c> status are always left untouched.
/// </summary>
public enum SessionReminderSync
{
    /// <summary>No reminder change (create, review, complete, read paths).</summary>
    None,

    /// <summary>
    /// Delete non-sent (Pending/Suppressed) rows and insert the supplied occurrences
    /// as Pending. Used after schedule and update, computed via
    /// <see cref="CommunitySession.ComputeReminderOccurrences"/>.
    /// </summary>
    Recompute,

    /// <summary>Mark Pending rows Suppressed. Used after cancel. Sent rows stay Sent.</summary>
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
        CancellationToken ct);

    Task UpdateAsync(
        CommunitySession session,
        long expectedVersion,
        string actorId,
        string reason,
        DateTimeOffset recordedAtUtc,
        SessionReminderSync reminderSync,
        IReadOnlyList<SessionReminderOccurrence>? occurrences,
        CancellationToken ct);

    Task<IReadOnlyList<CommunitySession>> ListPublicAsync(int take, CancellationToken ct);

    Task<int> CountRevisionsAsync(Guid sessionId, CancellationToken ct);

    Task<int> CountPendingRemindersAsync(Guid sessionId, CancellationToken ct);

    Task<int> CountSuppressedRemindersAsync(Guid sessionId, CancellationToken ct);
}
