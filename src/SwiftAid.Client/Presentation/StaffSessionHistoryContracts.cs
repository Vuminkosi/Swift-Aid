using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Client.Presentation;

/// <summary>Staff-only, bounded session revision history.</summary>
public interface IStaffSessionHistoryClient
{
    Task<UiCapabilityResult<SessionHistoryPage>> ReadAsync(Guid sessionId, long? beforeVersion, CancellationToken cancellationToken = default);
}
