using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Surfaces.Account;

namespace SwiftAid.Ui.Tests;

public sealed class ChangePasswordSurfaceTests
{
    [Fact]
    public async Task ChangePasswordSurface_SignedOutOffersSignIn()
    {
        var markup = await RenderAsync(ChangePasswordSnapshot.SignedOut);

        Assert.Contains("Sign in to change your password.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangePasswordSurface_ReadyShowsFieldsAndPolicy()
    {
        var markup = await RenderAsync(
            new ChangePasswordSnapshot(ChangePasswordViewState.Ready, false));

        Assert.Contains("Change password", markup, StringComparison.Ordinal);
        Assert.Contains("Current password", markup, StringComparison.Ordinal);
        Assert.Contains("New password", markup, StringComparison.Ordinal);
        Assert.Contains("Confirm new password", markup, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"current-password\"", markup, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"new-password\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangePasswordSurface_SuccessAndErrorCopy()
    {
        var ok = await RenderAsync(
            new ChangePasswordSnapshot(ChangePasswordViewState.Ready, false, "Your password was changed.", false));
        Assert.Contains("Your password was changed.", ok, StringComparison.Ordinal);

        var bad = await RenderAsync(
            new ChangePasswordSnapshot(ChangePasswordViewState.Ready, false, "The new passwords do not match.", true));
        Assert.Contains("The new passwords do not match.", bad, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(ChangePasswordSnapshot snapshot)
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ChangePasswordSurface>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(ChangePasswordSurface.Snapshot)] = snapshot,
                    [nameof(ChangePasswordSurface.OnRefresh)] = EventCallback.Factory.Create(
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
