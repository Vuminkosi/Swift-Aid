namespace SwiftAid.UI.Components.Onboarding;

/// <summary>
/// Local first-run presentation progress for the onboarding experience.
/// These values describe which introductory UI has been shown on this device.
/// They are never proof of verified identity, active membership, or authorization;
/// authentication, profile, and membership state always come from backend contracts.
/// </summary>
public enum OnboardingFlowState
{
    NotStarted = 0,
    IntroInProgress = 1,
    PublicEntryReady = 2,
    RegistrationStarted = 3,
    PendingVerification = 4,
    VerificationFailed = 5,
    ProfileIncomplete = 6,
    ProfileReady = 7,
    MembershipPending = 8,
    MemberReady = 9,
    WorkspaceBootstrapRequired = 10,
    WorkspaceReady = 11,
    Completed = 12,
    Interrupted = 13
}

/// <summary>
/// Pure routing helpers for the onboarding state machine.
/// Destination authorization is decided by backend state, not by these hints.
/// </summary>
public static class OnboardingFlowRoutes
{
    public static bool IsTerminal(OnboardingFlowState state) =>
        state is OnboardingFlowState.Completed or OnboardingFlowState.MemberReady or OnboardingFlowState.WorkspaceReady;

    public static bool KeepsGuestEntryAvailable(OnboardingFlowState state) =>
        state is not (OnboardingFlowState.Completed or OnboardingFlowState.MemberReady or OnboardingFlowState.WorkspaceReady);

    public static string SuggestNextHref(OnboardingFlowState state) => state switch
    {
        OnboardingFlowState.NotStarted => "/welcome",
        OnboardingFlowState.IntroInProgress => "/welcome",
        OnboardingFlowState.PublicEntryReady => "/",
        OnboardingFlowState.RegistrationStarted => "/account",
        OnboardingFlowState.PendingVerification => "/account",
        OnboardingFlowState.VerificationFailed => "/account",
        OnboardingFlowState.ProfileIncomplete => "/account",
        OnboardingFlowState.ProfileReady => "/account",
        OnboardingFlowState.MembershipPending => "/account",
        OnboardingFlowState.MemberReady => "/",
        OnboardingFlowState.WorkspaceBootstrapRequired => "/account",
        OnboardingFlowState.WorkspaceReady => "/",
        OnboardingFlowState.Completed => "/",
        OnboardingFlowState.Interrupted => "/welcome",
        _ => "/"
    };
}
