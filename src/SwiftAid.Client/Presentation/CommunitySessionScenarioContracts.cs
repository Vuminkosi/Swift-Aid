using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Client.Presentation;

public sealed record CommunitySessionScenarioEvent(
    long Sequence,
    string Action,
    string Summary,
    DateTimeOffset OccurredAtUtc,
    long Version,
    bool IsSynthetic);

public sealed record CommunitySessionScenarioSnapshot(
    Guid ScenarioSessionId,
    long Version,
    CommunitySessionView? Session,
    IReadOnlyList<PublicSessionView> PublicSessions,
    IReadOnlyList<CommunitySessionScenarioEvent> Audit,
    bool IsSynthetic,
    string Notice);

/// <summary>Runs isolated session-management examples using the existing versioned command shapes.</summary>
public interface ICommunitySessionScenarioClient
{
    Task<UiCapabilityResult<CommunitySessionScenarioSnapshot>> GetSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<CommunitySessionScenarioSnapshot>> ExecuteAsync(
        SessionCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed production implementation for isolated session-management examples.</summary>
public sealed class UnavailableCommunitySessionScenarioClient : ICommunitySessionScenarioClient
{
    public Task<UiCapabilityResult<CommunitySessionScenarioSnapshot>> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<CommunitySessionScenarioSnapshot>("community-session-scenario"));
    }

    public Task<UiCapabilityResult<CommunitySessionScenarioSnapshot>> ExecuteAsync(
        SessionCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);
        return Task.FromResult(UiCapabilityResults.Unavailable<CommunitySessionScenarioSnapshot>("community-session-scenario"));
    }
}
