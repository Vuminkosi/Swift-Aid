using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using SwiftAid.Gateway.Components;

namespace SwiftAid.Ui.Tests;

public sealed class RedirectToSignInTests
{
    [Fact]
    public async Task Redirect_PreservesLocalRouteQueryAndFragment()
    {
        var location = await RedirectAsync(
            "https://example.test/app/",
            "https://example.test/app/case/123?tab=history#latest");

        Assert.Equal(
            "/sign-in?returnUrl=%2Fcase%2F123%3Ftab%3Dhistory%23latest",
            location);
    }

    [Fact]
    public async Task Redirect_UsesHomeWhenCurrentLocationIsBaseRoute()
    {
        var location = await RedirectAsync(
            "https://example.test/app/",
            "https://example.test/app/");

        Assert.Equal("/sign-in?returnUrl=%2F", location);
    }

    [Fact]
    public async Task Redirect_DoesNotCarryAnOpenRedirectPath()
    {
        var location = await RedirectAsync(
            "https://example.test/app/",
            "https://example.test/app///outside.test/private");

        Assert.Equal("/sign-in?returnUrl=%2F", location);
    }

    private static async Task<string?> RedirectAsync(string baseUri, string currentUri)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        var navigation = new TestNavigationManager(baseUri, currentUri);
        services.AddSingleton<NavigationManager>(navigation);

        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<RedirectToSignIn>();
        });

        return navigation.LastNavigatedLocation;
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string baseUri, string currentUri) => Initialize(baseUri, currentUri);

        public string? LastNavigatedLocation { get; private set; }

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            LastNavigatedLocation = uri;
    }
}
