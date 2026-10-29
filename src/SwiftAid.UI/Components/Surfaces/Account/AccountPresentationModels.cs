namespace SwiftAid.UI.Components.Surfaces.Account;

public enum AccountViewState
{
    Loading,
    Available,
    Unavailable,
    Denied
}

public enum AccountProviderState
{
    Connected,
    NotConnected,
    Pending,
    Unavailable
}

public sealed record AccountProviderView(
    string Id,
    string Name,
    AccountProviderState State,
    string? StatusMessage = null);

public sealed record AccountWorkspaceView(
    string Id,
    string Name,
    bool IsSelected);

public sealed record AccountPresentationSnapshot(
    AccountViewState ViewState,
    bool IsSignedIn,
    string? DisplayName,
    IReadOnlyList<AccountProviderView> Providers,
    IReadOnlyList<AccountWorkspaceView> Workspaces,
    bool WorkspacesAvailable,
    string? SelectedWorkspaceId,
    string? SelectedWorkspaceName,
    string? StatusMessage = null,
    bool StatusIsError = false)
{
    public static AccountPresentationSnapshot Unavailable { get; } = new(
        AccountViewState.Unavailable,
        IsSignedIn: false,
        DisplayName: null,
        Providers: Array.Empty<AccountProviderView>(),
        Workspaces: Array.Empty<AccountWorkspaceView>(),
        WorkspacesAvailable: false,
        SelectedWorkspaceId: null,
        SelectedWorkspaceName: null,
        StatusMessage: "Account details are unavailable right now.");
}

public enum ProfileViewState
{
    Loading,
    Ready,
    SignedOut,
    Unavailable
}

public sealed record ProfileSnapshot(
    ProfileViewState ViewState,
    string? DisplayName,
    string? Phone,
    string? Country,
    string? Region,
    string? Locality,
    string? Email,
    bool EmailVerified,
    bool IsSaving,
    string? StatusMessage = null,
    bool StatusIsError = false)
{
    public static ProfileSnapshot Loading { get; } = new(
        ProfileViewState.Loading, null, null, null, null, null, null, false, false);

    public static ProfileSnapshot SignedOut { get; } = new(
        ProfileViewState.SignedOut, null, null, null, null, null, null, false, false,
        "Sign in to view your profile.", true);

    public static ProfileSnapshot Unavailable { get; } = new(
        ProfileViewState.Unavailable, null, null, null, null, null, null, false, false,
        "Your profile is temporarily unavailable.", true);
}

public sealed record ProfileEditRequest(
    string? DisplayName,
    string? Phone,
    string? Country,
    string? Region,
    string? Locality);
