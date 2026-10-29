using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Client.Presentation;

/// <summary>Presentation port for the protected community-session editor.</summary>
public interface IStaffSessionEditorClient
{
    Task<UiCapabilityResult<string>> GetCurrentActorIdAsync(CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<StaffSessionManagementCapabilities>> GetManagementCapabilitiesAsync(
        Guid? sessionId,
        long? expectedVersion,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<CommunitySessionView>> GetStaffAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<SessionCommandResult>> ExecuteAsync(
        SessionCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>Current, action-specific authority for creating or changing one session.</summary>
public sealed record StaffSessionManagementCapabilities(
    bool CanCreate,
    bool CanEdit,
    bool CanReview,
    bool CanSchedule,
    bool CanCancel,
    bool CanComplete,
    Guid? SessionId,
    long? Version,
    string? Status,
    bool IsCurrent);

/// <summary>Fail-closed editor port when no authorized session service is configured.</summary>
public sealed class UnavailableStaffSessionEditorClient : IStaffSessionEditorClient
{
    public Task<UiCapabilityResult<string>> GetCurrentActorIdAsync(CancellationToken cancellationToken = default) =>
        Unavailable<string>("community-session-editor", cancellationToken);

    public Task<UiCapabilityResult<StaffSessionManagementCapabilities>> GetManagementCapabilitiesAsync(
        Guid? sessionId,
        long? expectedVersion,
        CancellationToken cancellationToken = default) =>
        Unavailable<StaffSessionManagementCapabilities>("community-session-editor", cancellationToken);

    public Task<UiCapabilityResult<CommunitySessionView>> GetStaffAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        Unavailable<CommunitySessionView>("community-session-editor", cancellationToken);

    public Task<UiCapabilityResult<SessionCommandResult>> ExecuteAsync(
        SessionCommand command,
        CancellationToken cancellationToken = default) =>
        Unavailable<SessionCommandResult>("community-session-editor", cancellationToken);

    private static Task<UiCapabilityResult<T>> Unavailable<T>(string capability, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<T>(capability));
    }
}
