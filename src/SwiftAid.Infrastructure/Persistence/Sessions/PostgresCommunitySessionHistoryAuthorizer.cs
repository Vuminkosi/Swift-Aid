using SwiftAid.Application.Identity;
using SwiftAid.Application.Sessions;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>Resolves a session's persisted organization before checking current owner authority.</summary>
public sealed class PostgresCommunitySessionHistoryAuthorizer(
    ICommunitySessionStore sessions,
    IOrganizationOwnerAuthorizer owners) : ICommunitySessionHistoryAuthorizer
{
    private readonly ICommunitySessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IOrganizationOwnerAuthorizer _owners = owners ?? throw new ArgumentNullException(nameof(owners));

    public async Task<bool> CanReadAsync(string actorId, Guid sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId) || sessionId == Guid.Empty) return false;
        var organizationId = await _sessions.GetOrganizationIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(organizationId) &&
            await _owners.IsActiveOwnerAsync(actorId, organizationId, cancellationToken).ConfigureAwait(false);
    }
}
