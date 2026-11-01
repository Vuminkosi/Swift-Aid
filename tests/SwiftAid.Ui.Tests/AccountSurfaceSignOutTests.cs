using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Surfaces.Account;
using SwiftAid.UI.Components.Surfaces.Public;

namespace SwiftAid.Ui.Tests;

public sealed class AccountSurfaceSignOutTests
{
    [Fact]
    public async Task WorkspaceFailure_OffersOrganizationManagementWithoutClaimingTheUserIsOffline()
    {
        var markup = await RenderAsync(SignedInSnapshot(), withSignOutHandler: true, organizationsHref: "/organizations");
        Assert.Contains("Organizations and invitations", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/organizations\"", markup, StringComparison.Ordinal);
        Assert.Contains("Refresh to try again.", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("back online", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedInSnapshot_OffersSignOut()
    {
        var markup = await RenderAsync(SignedInSnapshot(), withSignOutHandler: true);

        Assert.Contains("Sign out", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign-out is unavailable right now.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedInSnapshot_WithoutSignOutHandler_ExplainsUnavailability()
    {
        var markup = await RenderAsync(SignedInSnapshot(), withSignOutHandler: false);

        Assert.Contains("Sign-out is unavailable right now.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuestSnapshot_DoesNotOfferSignOut()
    {
        var markup = await RenderAsync(AccountPresentationSnapshot.Unavailable, withSignOutHandler: true);

        Assert.DoesNotContain("Sign out", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuestSnapshot_OffersSignInRegistrationAndGuestReportRoutes()
    {
        var markup = await RenderAsync(
            new AccountPresentationSnapshot(
                AccountViewState.Available,
                IsSignedIn: false,
                DisplayName: null,
                Providers: Array.Empty<AccountProviderView>(),
                Workspaces: Array.Empty<AccountWorkspaceView>(),
                WorkspacesAvailable: true,
                SelectedWorkspaceId: null,
                SelectedWorkspaceName: null),
            withSignOutHandler: false,
            signInHref: "/sign-in?returnUrl=%2Fdashboard",
            registerHref: "/register",
            guestReportHref: "/report");

        Assert.Contains("Choose how to continue", markup, StringComparison.Ordinal);
        Assert.Contains("Sign in", markup, StringComparison.Ordinal);
        Assert.Contains("Create account", markup, StringComparison.Ordinal);
        Assert.Contains("Continue to a guest report", markup, StringComparison.Ordinal);
        Assert.Contains("/sign-in?returnUrl=%2Fdashboard", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/register\"", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/report\"", markup, StringComparison.Ordinal);
        Assert.Contains("Sign-in required", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign-in methods", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Unavailable", markup, StringComparison.Ordinal);
    }

    private static AccountPresentationSnapshot SignedInSnapshot() => new(
        AccountViewState.Available,
        IsSignedIn: true,
        DisplayName: "Test User",
        Providers:
        [
            new AccountProviderView("native-oidc", "Configured sign-in", AccountProviderState.Connected)
        ],
        Workspaces: Array.Empty<AccountWorkspaceView>(),
        WorkspacesAvailable: false,
        SelectedWorkspaceId: null,
        SelectedWorkspaceName: null);

    private static async Task<string> RenderAsync(
        AccountPresentationSnapshot snapshot,
        bool withSignOutHandler,
        string? signInHref = null,
        string? registerHref = null,
        string? guestReportHref = null,
        string? organizationsHref = null)
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = new Dictionary<string, object?>
            {
                [nameof(AccountSurface.Snapshot)] = snapshot,
                [nameof(AccountSurface.OnRefresh)] = EventCallback.Factory.Create(new object(), static () => { }),
                [nameof(AccountSurface.SignInHref)] = signInHref,
                [nameof(AccountSurface.RegisterHref)] = registerHref,
                [nameof(AccountSurface.GuestReportHref)] = guestReportHref,
                [nameof(AccountSurface.OrganizationsHref)] = organizationsHref
            };
            if (withSignOutHandler)
            {
                parameters[nameof(AccountSurface.OnSignOut)] =
                    EventCallback.Factory.Create(new object(), static () => { });
            }

            var component = await renderer.RenderComponentAsync<AccountSurface>(
                ParameterView.FromDictionary(parameters));
            return component.ToHtmlString();
        });
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(NoOpJsRuntime.Instance);
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        return services.BuildServiceProvider();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class NoOpJsRuntime : JSRuntime
    {
        public static readonly NoOpJsRuntime Instance = new();

        protected override void BeginInvokeJS(
            long taskId,
            string identifier,
            string? argsJson,
            JSCallResultType resultType,
            long targetInstanceId)
        {
        }

        protected override void EndInvokeDotNet(
            DotNetInvocationInfo callInfo,
            in DotNetInvocationResult result)
        {
        }
    }

    private static HtmlRenderer CreateRenderer(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());
}
