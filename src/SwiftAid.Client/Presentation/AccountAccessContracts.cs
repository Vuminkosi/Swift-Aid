namespace SwiftAid.Client.Presentation;

public sealed record AccountProviderOption(
    string Id,
    string Name,
    bool IsAvailable,
    string? StatusMessage = null,
    bool IsConnected = false);

public sealed record AccountPresentationState(
    bool IsSignedIn,
    string? DisplayName,
    IReadOnlyList<AccountProviderOption> Providers,
    string? StatusMessage = null,
    bool StatusIsError = false);

public sealed record WorkspaceAccessOption(string Id, string Name);

public sealed record WorkspaceAccessState(
    IReadOnlyList<WorkspaceAccessOption> Workspaces,
    string? SelectedWorkspaceId,
    string? StatusMessage = null);

/// <summary>Authentication presentation actions; this port does not establish workspace permissions.</summary>
public interface IAccountPresentationClient
{
    Task<UiCapabilityResult<AccountPresentationState>> GetStateAsync(CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<AccountPresentationState>> BeginSignInAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<AccountPresentationState>> SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>Lists and selects a display context. Selection never grants identity, membership, or data access.</summary>
public interface IWorkspaceAccessClient
{
    Task<UiCapabilityResult<WorkspaceAccessState>> GetStateAsync(CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<WorkspaceAccessState>> SelectWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableAccountPresentationClient : IAccountPresentationClient
{
    public Task<UiCapabilityResult<AccountPresentationState>> GetStateAsync(CancellationToken cancellationToken = default) =>
        Unavailable(cancellationToken);

    public Task<UiCapabilityResult<AccountPresentationState>> BeginSignInAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        return Unavailable(cancellationToken);
    }

    public Task<UiCapabilityResult<AccountPresentationState>> SignOutAsync(CancellationToken cancellationToken = default) =>
        Unavailable(cancellationToken);

    private static Task<UiCapabilityResult<AccountPresentationState>> Unavailable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<AccountPresentationState>("account-session"));
    }
}

public sealed class UnavailableWorkspaceAccessClient : IWorkspaceAccessClient
{
    public Task<UiCapabilityResult<WorkspaceAccessState>> GetStateAsync(CancellationToken cancellationToken = default) =>
        Unavailable(cancellationToken);

    public Task<UiCapabilityResult<WorkspaceAccessState>> SelectWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);
        return Unavailable(cancellationToken);
    }

    private static Task<UiCapabilityResult<WorkspaceAccessState>> Unavailable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<WorkspaceAccessState>("workspace-display-context"));
    }
}
