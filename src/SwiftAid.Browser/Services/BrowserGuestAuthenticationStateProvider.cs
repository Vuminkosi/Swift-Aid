using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace SwiftAid.Browser.Services;

/// <summary>
/// Reflects the Gateway-validated cookie state rendered on the guest host element.
/// It supplies UI state only; Gateway endpoints remain responsible for authorization.
/// </summary>
internal sealed class BrowserGuestAuthenticationStateProvider(IJSRuntime jsRuntime)
    : AuthenticationStateProvider, IAsyncDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> _module = new(() =>
        jsRuntime.InvokeAsync<IJSObjectReference>("import", "js/guest-registration.js").AsTask());

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var module = await _module.Value.ConfigureAwait(false);
            var isAuthenticated = await module.InvokeAsync<bool>("isAuthenticated").ConfigureAwait(false);
            var identity = isAuthenticated
                ? new ClaimsIdentity(authenticationType: "SwiftAidGatewayCookie")
                : new ClaimsIdentity();

            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch (JSException)
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_module.IsValueCreated)
        {
            return;
        }

        try
        {
            await (await _module.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            // The page is already closing; there is no live JavaScript runtime to release.
        }
    }
}
