using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Client.Presentation.PublicWorkflows;

/// <summary>Read-only public session discovery and copy-only invitation access.</summary>
public interface IPublicCommunitySessionClient
{
    /// <summary>Lists public sessions using the service's supported limit range of 1 through 100.</summary>
    Task<UiCapabilityResult<IReadOnlyList<PublicSessionView>>> ListPublicAsync(
        int? limit = null,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<PublicSessionPage>> QueryPublicAsync(
        PublicSessionQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a public projection, or a successful null for an unknown or nonpublic session.</summary>
    Task<UiCapabilityResult<PublicSessionView>> GetPublicAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<string>> GetInvitationTextAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<string>> GetInvitationTextForVersionAsync(
        Guid sessionId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<string>("public-community-session-invitation"));
    }
}
