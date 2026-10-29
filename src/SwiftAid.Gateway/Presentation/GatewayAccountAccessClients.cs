using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Extensions.Options;
using SwiftAid.Client.Presentation;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Maps the existing Gateway sign-in routes and authenticated principal to account presentation.</summary>
public sealed class GatewayAccountPresentationClient(
    AuthenticationStateProvider authenticationStateProvider,
    IOptions<FederationOptions> federationOptions,
    NavigationManager navigationManager,
    IJSRuntime jsRuntime) : IAccountPresentationClient
{
    private const string Capability = "account-session";
    private readonly AuthenticationStateProvider _authenticationStateProvider = authenticationStateProvider;
    private readonly FederationOptions _federationOptions = federationOptions.Value;
    private readonly NavigationManager _navigationManager = navigationManager;
    private readonly IJSRuntime _jsRuntime = jsRuntime;

    public async Task<UiCapabilityResult<AccountPresentationState>> GetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var authentication = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            var principal = authentication.User;
            var isSignedIn = principal.Identity?.IsAuthenticated == true;
            var displayName = isSignedIn
                ? principal.FindFirst(ClaimTypes.Name)?.Value ?? principal.Identity?.Name
                : null;
            var state = new AccountPresentationState(
                isSignedIn,
                displayName,
                GetProviders(),
                isSignedIn ? "Workspace access remains controlled by your account permissions." : null);
            return Success(state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UiCapabilityResults.Unavailable<AccountPresentationState>(Capability);
        }
    }

    public Task<UiCapabilityResult<AccountPresentationState>> BeginSignInAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = GetProviders().FirstOrDefault(item => string.Equals(item.Id, providerId, StringComparison.Ordinal));
        if (provider is null || !provider.IsAvailable)
        {
            return Task.FromResult(new UiCapabilityResult<AccountPresentationState>(
                UiCapabilityResultCode.Unavailable,
                null,
                Metadata(isAvailable: false),
                "That sign-in option is unavailable."));
        }

        var returnUrl = Uri.EscapeDataString("/account");
        _navigationManager.NavigateTo($"/sign-in?returnUrl={returnUrl}", forceLoad: true);
        return Task.FromResult(Success(new AccountPresentationState(
            false,
            null,
            GetProviders(),
            "Choose a sign-in method to continue.")));
    }

    public async Task<UiCapabilityResult<AccountPresentationState>> SignOutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                cancellationToken,
                "/auth/account-session.js");
            var signedOut = await module.InvokeAsync<bool>("signOut", cancellationToken);
            if (!signedOut)
                return new UiCapabilityResult<AccountPresentationState>(
                    UiCapabilityResultCode.Retryable,
                    null,
                    Metadata(),
                    "Sign-out could not be confirmed. Try again.");

            _navigationManager.NavigateTo("/account", forceLoad: true);
            return Success(new AccountPresentationState(false, null, GetProviders(), "You are signed out."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new UiCapabilityResult<AccountPresentationState>(
                UiCapabilityResultCode.Retryable,
                null,
                Metadata(),
                "Sign-out could not be confirmed. Try again.");
        }
    }

    private IReadOnlyList<AccountProviderOption> GetProviders()
    {
        var providers = new List<AccountProviderOption>();
        if (_federationOptions.Google.Enabled)
            providers.Add(new("google", "Google", _federationOptions.Google.CallbackVerified));
        if (_federationOptions.Facebook.Enabled)
            providers.Add(new("facebook", "Facebook", _federationOptions.Facebook.CallbackVerified));
        providers.AddRange(_federationOptions.OrganizationProviders.Select(provider => new AccountProviderOption(
            provider.ProviderId,
            string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.ProviderId : provider.DisplayName,
            provider.CallbackVerified)));
        return providers;
    }

    private static UiCapabilityResult<AccountPresentationState> Success(AccountPresentationState state) =>
        new(UiCapabilityResultCode.Success, state, Metadata(), state.StatusMessage);

    private static UiCapabilityMetadata Metadata(bool isAvailable = true) => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        isAvailable);
}

/// <summary>Existing Gateway has no authoritative workspace catalog/selection endpoint.</summary>
public sealed class GatewayUnavailableWorkspaceAccessClient : IWorkspaceAccessClient
{
    private static readonly UnavailableWorkspaceAccessClient Inner = new();

    public Task<UiCapabilityResult<WorkspaceAccessState>> GetStateAsync(CancellationToken cancellationToken = default) =>
        Inner.GetStateAsync(cancellationToken);

    public Task<UiCapabilityResult<WorkspaceAccessState>> SelectWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken = default) =>
        Inner.SelectWorkspaceAsync(workspaceId, cancellationToken);
}
