using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Client.Presentation.PublicWorkflows;

/// <summary>Read-only public session discovery and copy-only invitation access.</summary>
public interface IPublicCommunitySessionClient
{
    /// <summary>Lists public sessions using the service's supported limit range of 1 through 100.</summary>
    Task<UiCapabilityResult<IReadOnlyList<PublicSessionView>>> ListPublicAsync(
        int? limit = null,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<string>> GetInvitationTextAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}
