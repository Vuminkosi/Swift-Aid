using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Sessions;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Gateway.Sessions;

/// <summary>Uses the current circuit identity and the independently authorized history reader.</summary>
public sealed class GatewayStaffSessionHistoryClient(SessionGatewayClient sessionClient, IServiceProvider services) : IStaffSessionHistoryClient
{
    public async Task<UiCapabilityResult<SessionHistoryPage>> ReadAsync(Guid sessionId, long? beforeVersion, CancellationToken cancellationToken = default)
    {
        const string capability = "community-session-history";
        var actor = await sessionClient.GetCurrentActorIdAsync(cancellationToken).ConfigureAwait(false);
        var reader = services.GetService<ICommunitySessionHistoryReader>();
        if (string.IsNullOrWhiteSpace(actor) || reader is null)
            return UiCapabilityResults.Unavailable<SessionHistoryPage>(capability);
        try
        {
            var page = await reader.ReadAsync(sessionId, actor, beforeVersion, 25, cancellationToken).ConfigureAwait(false);
            var currentActor = await sessionClient.GetCurrentActorIdAsync(cancellationToken).ConfigureAwait(false);
            if (page is null || page.SessionId != sessionId || !string.Equals(actor, currentActor, StringComparison.Ordinal))
                return UiCapabilityResults.Unavailable<SessionHistoryPage>(capability);
            return new UiCapabilityResult<SessionHistoryPage>(UiCapabilityResultCode.Success, page,
                new UiCapabilityMetadata(capability, UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<SessionHistoryPage>(capability);
        }
    }
}
