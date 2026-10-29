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
    bool StatusIsError = false,
    string? AvatarUrl = null,
    bool IsSavingAvatar = false)
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

public sealed record ProfileAvatarUploadRequest(string ContentType, Stream Content);

public enum OrganizationsViewState
{
    Loading,
    Ready,
    SignedOut,
    Unavailable
}

public sealed record MembershipSnapshot(
    string OrganizationId,
    string OrganizationStatus,
    string MembershipStatus,
    IReadOnlyList<string> WorkspaceIds);

public sealed record OrganizationOptionSnapshot(string OrganizationId, string Status);

public sealed record MembershipRequestSnapshot(
    string RequestId,
    string OrganizationId,
    string Status,
    string? Reason);

public sealed record OrganizationsSnapshot(
    OrganizationsViewState ViewState,
    IReadOnlyList<MembershipSnapshot> Memberships,
    IReadOnlyList<MembershipRequestSnapshot> Requests,
    IReadOnlyList<OrganizationOptionSnapshot> Directory,
    bool IsSaving,
    string? StatusMessage = null,
    bool StatusIsError = false)
{
    public static OrganizationsSnapshot Loading { get; } = new(
        OrganizationsViewState.Loading, [], [], [], false);

    public static OrganizationsSnapshot SignedOut { get; } = new(
        OrganizationsViewState.SignedOut, [], [], [], false,
        "Sign in to view your workspaces.", true);

    public static OrganizationsSnapshot Unavailable { get; } = new(
        OrganizationsViewState.Unavailable, [], [], [], false,
        "Your workspaces are temporarily unavailable.", true);
}

public sealed record MembershipJoinRequest(string OrganizationId, string? Reason);

public enum NotificationsViewState
{
    Loading,
    Ready,
    SignedOut,
    Unavailable
}

public sealed record NotificationsSnapshot(
    NotificationsViewState ViewState,
    bool WorkReminders,
    bool SessionUpdates,
    bool IsSaving,
    string? StatusMessage = null,
    bool StatusIsError = false)
{
    public static NotificationsSnapshot Loading { get; } = new(
        NotificationsViewState.Loading, true, true, false);

    public static NotificationsSnapshot SignedOut { get; } = new(
        NotificationsViewState.SignedOut, true, true, false,
        "Sign in to view notification preferences.", true);

    public static NotificationsSnapshot Unavailable { get; } = new(
        NotificationsViewState.Unavailable, true, true, false,
        "Notification preferences are temporarily unavailable.", true);
}

public sealed record NotificationPreferenceChange(bool WorkReminders, bool SessionUpdates);

public enum ChangePasswordViewState
{
    Loading,
    Ready,
    SignedOut,
    Unavailable
}

/// <summary>Password change. Sessions stay device-local; no remote session list exists.</summary>
public sealed record ChangePasswordSnapshot(
    ChangePasswordViewState ViewState,
    bool IsSaving,
    string? StatusMessage = null,
    bool StatusIsError = false)
{
    public static ChangePasswordSnapshot Loading { get; } = new(
        ChangePasswordViewState.Loading, false);

    public static ChangePasswordSnapshot SignedOut { get; } = new(
        ChangePasswordViewState.SignedOut, false,
        "Sign in to change your password.", true);

    public static ChangePasswordSnapshot Unavailable { get; } = new(
        ChangePasswordViewState.Unavailable, false,
        "Password changes are temporarily unavailable.", true);
}

/// <summary>Null <see cref="ChangePasswordRequest.NewPassword"/> signals a confirmation
/// mismatch; pages report it without calling the backend.</summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public enum WelcomeCompleteViewState
{
    Loading,
    Ready,
    SignedOut,
    Unavailable
}

/// <summary>Account-ready completion. Destinations derive from verified state only.</summary>
public sealed record WelcomeCompleteSnapshot(
    WelcomeCompleteViewState ViewState,
    string? DisplayName,
    bool HasActiveWorkspace,
    bool HasPendingRequest,
    string? StatusMessage = null,
    bool StatusIsError = false)
{
    public static WelcomeCompleteSnapshot Loading { get; } = new(
        WelcomeCompleteViewState.Loading, null, false, false);

    public static WelcomeCompleteSnapshot SignedOut { get; } = new(
        WelcomeCompleteViewState.SignedOut, null, false, false,
        "Sign in to view this page.", true);

    public static WelcomeCompleteSnapshot Unavailable { get; } = new(
        WelcomeCompleteViewState.Unavailable, null, false, false,
        "Your account details are temporarily unavailable.", true);
}

public enum ParticipationPreferenceViewState
{
    Loading,
    Ready,
    SignedOut,
    Unavailable
}

public sealed record ParticipationOptionSnapshot(
    string Key,
    string Title,
    string Description);

/// <summary>Participation preference. A device-local personalization choice;
/// never proof of membership and never a source of staff permissions.</summary>
public sealed record ParticipationPreferenceSnapshot(
    ParticipationPreferenceViewState ViewState,
    string? SelectedKey,
    bool HasActiveMembership,
    bool HasPendingRequest,
    string? StatusMessage = null,
    bool StatusIsError = false)
{
    public static IReadOnlyList<ParticipationOptionSnapshot> Options { get; } =
    [
        new("community-health-worker", "Community Health Worker",
            "Fieldwork, case coordination, community education"),
        new("nurse-clinician", "Nurse / Clinician",
            "Clinical or health services"),
        new("community-leader", "Community Leader",
            "Outreach and engagement"),
        new("program-coordinator", "Program Coordinator",
            "Planning and reporting"),
        new("community-member", "Community Member",
            "Public information and events")
    ];

    public static bool IsKnownKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        Options.Any(option => string.Equals(option.Key, key, StringComparison.Ordinal));

    public static ParticipationPreferenceSnapshot Loading { get; } = new(
        ParticipationPreferenceViewState.Loading, null, false, false);

    public static ParticipationPreferenceSnapshot SignedOut { get; } = new(
        ParticipationPreferenceViewState.SignedOut, null, false, false,
        "Sign in to choose how you participate.", true);

    public static ParticipationPreferenceSnapshot Unavailable { get; } = new(
        ParticipationPreferenceViewState.Unavailable, null, false, false,
        "Participation choices are temporarily unavailable.", true);
}
