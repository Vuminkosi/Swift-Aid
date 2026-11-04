using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Application.Sessions;

/// <summary>Current organization-owner authorization for staff history; absent providers deny access.</summary>
public interface ICommunitySessionHistoryAuthorizer
{
    Task<bool> CanReadAsync(string actorId, Guid sessionId, CancellationToken cancellationToken);
}

public interface ICommunitySessionHistoryReader
{
    Task<SessionHistoryPage?> ReadAsync(Guid sessionId, string actorId, long? beforeVersion, int limit, CancellationToken cancellationToken);
}

/// <summary>Reads bounded history only after current session-specific authorization.</summary>
public sealed class CommunitySessionHistoryReader(
    ICommunitySessionStore store,
    ICommunitySessionHistoryAuthorizer? authorizer = null) : ICommunitySessionHistoryReader
{
    private readonly ICommunitySessionStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<SessionHistoryPage?> ReadAsync(Guid sessionId, string actorId, long? beforeVersion, int limit, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(actorId) || beforeVersion is <= 0 || limit is < 1 or > 100)
            throw new ArgumentException("A valid session, actor, cursor and page size are required.");
        cancellationToken.ThrowIfCancellationRequested();
        if (authorizer is null || !await authorizer.CanReadAsync(actorId, sessionId, cancellationToken).ConfigureAwait(false))
            return null;
        var records = await _store.ReadHistoryAsync(sessionId, beforeVersion, limit + 1, cancellationToken).ConfigureAwait(false);
        if (!await authorizer.CanReadAsync(actorId, sessionId, cancellationToken).ConfigureAwait(false)) return null;
        var page = records.Take(limit).ToArray();
        return new SessionHistoryPage(sessionId, page, records.Count > limit ? page[^1].Version : null);
    }
}
