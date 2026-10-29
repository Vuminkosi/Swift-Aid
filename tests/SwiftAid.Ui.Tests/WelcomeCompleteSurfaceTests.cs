using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Surfaces.Account;

namespace SwiftAid.Ui.Tests;

public sealed class WelcomeCompleteSurfaceTests
{
    [Fact]
    public async Task WelcomeCompleteSurface_SignedOutOffersSignIn()
    {
        var markup = await RenderAsync(WelcomeCompleteSnapshot.SignedOut);

        Assert.Contains("Sign in to view this page.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WelcomeCompleteSurface_MemberWithoutWorkspaceIsInvitedToJoin()
    {
        var markup = await RenderAsync(new WelcomeCompleteSnapshot(
            WelcomeCompleteViewState.Ready,
            DisplayName: "Test User",
            HasActiveWorkspace: false,
            HasPendingRequest: false));

        Assert.Contains("Welcome to SwiftAid, Test User!", markup, StringComparison.Ordinal);
        Assert.Contains("Continue to SwiftAid", markup, StringComparison.Ordinal);
        Assert.Contains("Join an organization", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WelcomeCompleteSurface_ActiveWorkspaceContinuesToWorkspace()
    {
        var markup = await RenderAsync(new WelcomeCompleteSnapshot(
            WelcomeCompleteViewState.Ready,
            DisplayName: null,
            HasActiveWorkspace: true,
            HasPendingRequest: false));

        Assert.Contains("Welcome to SwiftAid!", markup, StringComparison.Ordinal);
        Assert.Contains("Continue to your workspace", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WelcomeCompleteSurface_PendingRequestShowsStatus()
    {
        var markup = await RenderAsync(new WelcomeCompleteSnapshot(
            WelcomeCompleteViewState.Ready,
            DisplayName: null,
            HasActiveWorkspace: false,
            HasPendingRequest: true));

        Assert.Contains("pending review", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("View request status", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WelcomeCompleteSurface_NeverImpliesStaffAccess()
    {
        foreach (var snapshot in new[]
        {
            new WelcomeCompleteSnapshot(WelcomeCompleteViewState.Ready, "A", true, false),
            new WelcomeCompleteSnapshot(WelcomeCompleteViewState.Ready, "A", false, true),
            new WelcomeCompleteSnapshot(WelcomeCompleteViewState.Ready, "A", false, false)
        })
        {
            var markup = await RenderAsync(snapshot);
            Assert.Contains("does not by itself provide access", markup, StringComparison.Ordinal);
        }
    }

    private static async Task<string> RenderAsync(WelcomeCompleteSnapshot snapshot)
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WelcomeCompleteSurface>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(WelcomeCompleteSurface.Snapshot)] = snapshot,
                    [nameof(WelcomeCompleteSurface.OnRefresh)] = EventCallback.Factory.Create(
                        new object(), () => { })
                }));
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
