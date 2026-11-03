using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;

namespace SwiftAid.Client.Sessions;

/// <summary>Presentation adapter over the existing anonymous Gateway session client.</summary>
public sealed class HttpPublicCommunitySessionClient(CommunitySessionHttpClient client) : IPublicCommunitySessionClient
{
    public Task<UiCapabilityResult<IReadOnlyList<SwiftAid.Contracts.Sessions.PublicSessionView>>> ListPublicAsync(
        int? limit = null,
        CancellationToken cancellationToken = default) => client.ListPublicResultAsync(limit, cancellationToken);

    public Task<UiCapabilityResult<string>> GetInvitationTextAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) => client.GetInvitationTextResultAsync(sessionId, cancellationToken);

    public Task<UiCapabilityResult<SwiftAid.Contracts.Sessions.PublicSessionPage>> QueryPublicAsync(
        SwiftAid.Contracts.Sessions.PublicSessionQuery query,
        CancellationToken cancellationToken = default) => client.QueryPublicResultAsync(query, cancellationToken);

    public Task<UiCapabilityResult<SwiftAid.Contracts.Sessions.PublicSessionView>> GetPublicAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) => client.GetPublicResultAsync(sessionId, cancellationToken);
}
