using SwiftAid.Client.Authentication;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;
using SwiftAid.Mobile.Services.Authentication;

namespace SwiftAid.Mobile.Services.Account;

/// <summary>Uses the configured native OIDC session without exposing tokens or treating sign-in as authorization.</summary>
public sealed class MobileAccountPresentationClient(
    IMobileAuthenticationSession authentication,
    NativeLoginOptions options) : IAccountPresentationClient
{
    private const string Capability = "account-session";
    private const string ProviderId = "native-oidc";
    private readonly IMobileAuthenticationSession _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    private readonly NativeLoginOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<UiCapabilityResult<AccountPresentationState>> GetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var signedIn = await _authentication.IsSignedInAsync(cancellationToken).ConfigureAwait(false);
        var pendingSignOut = await _authentication.HasPendingSignOutAsync(cancellationToken).ConfigureAwait(false);
        var state = CreateState(signedIn, pendingSignOut
            ? "Sign-out is still being completed. Retry after connectivity is restored."
            : null,
            statusIsError: pendingSignOut);
        return Success(state);
    }

    public async Task<UiCapabilityResult<AccountPresentationState>> BeginSignInAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(providerId, ProviderId, StringComparison.Ordinal))
            return Invalid(CreateState(false, "Choose the configured sign-in option.", statusIsError: true));

        var signedIn = await _authentication.SignInAsync(cancellationToken).ConfigureAwait(false);
        var message = signedIn
            ? "Signed in. Workspace access remains governed by your account permissions."
            : "Sign-in was cancelled or could not be completed.";
        return signedIn
            ? Success(CreateState(true, message))
            : new UiCapabilityResult<AccountPresentationState>(
                UiCapabilityResultCode.Cancelled,
                CreateState(false, message, statusIsError: false),
                Metadata(),
                message);
    }

    public async Task<UiCapabilityResult<AccountPresentationState>> SignOutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _authentication.SignOutAsync(cancellationToken).ConfigureAwait(false);
        if (result.SignedOut)
            return Success(CreateState(false, "You are signed out. Your saved guest drafts remain on this device."));

        var message = result.DeviceNotificationsStopped
            ? "Sign-out could not be confirmed. Retry when connectivity is restored."
            : "Device notification access could not be stopped, so sign-out is still pending.";
        return new UiCapabilityResult<AccountPresentationState>(
            UiCapabilityResultCode.Retryable,
            CreateState(false, message, statusIsError: true),
            Metadata(),
            message);
    }

    private AccountPresentationState CreateState(bool signedIn, string? status = null, bool statusIsError = false) => new(
        signedIn,
        DisplayName: null,
        Providers:
        [
            new AccountProviderOption(
                ProviderId,
                "Configured sign-in",
                !string.IsNullOrWhiteSpace(_options.Authority) && !string.IsNullOrWhiteSpace(_options.ClientId),
                null,
                signedIn)
        ],
        status,
        statusIsError);

    private static UiCapabilityResult<AccountPresentationState> Success(AccountPresentationState state) =>
        new(UiCapabilityResultCode.Success, state, Metadata(), state.StatusMessage);

    private static UiCapabilityResult<AccountPresentationState> Invalid(AccountPresentationState state) =>
        new(UiCapabilityResultCode.InvalidRequest, state, Metadata(), state.StatusMessage);

    private static UiCapabilityMetadata Metadata() => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: true);
}

/// <summary>Mobile's real host has no staff session-editor endpoint, so this adapter fails closed.</summary>
public sealed class MobileUnavailableStaffSessionEditorClient : IStaffSessionEditorClient
{
    private static readonly UnavailableStaffSessionEditorClient Inner = new();

    public Task<UiCapabilityResult<string>> GetCurrentActorIdAsync(CancellationToken cancellationToken = default) =>
        Inner.GetCurrentActorIdAsync(cancellationToken);

    public Task<UiCapabilityResult<StaffSessionManagementCapabilities>> GetManagementCapabilitiesAsync(
        Guid? sessionId,
        long? expectedVersion,
        CancellationToken cancellationToken = default) =>
        Inner.GetManagementCapabilitiesAsync(sessionId, expectedVersion, cancellationToken);

    public Task<UiCapabilityResult<CommunitySessionView>> GetStaffAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Inner.GetStaffAsync(sessionId, cancellationToken);

    public Task<UiCapabilityResult<SessionCommandResult>> ExecuteAsync(SessionCommand command, CancellationToken cancellationToken = default) =>
        Inner.ExecuteAsync(command, cancellationToken);
}
