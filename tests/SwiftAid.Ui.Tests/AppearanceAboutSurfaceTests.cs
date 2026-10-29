using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Surfaces.Public;
using SwiftAid.UI.Components.Theme;

namespace SwiftAid.Ui.Tests;

public sealed class AppearanceAboutSurfaceTests
{
    [Fact]
    public async Task ThemePreferenceControl_OffersSystemLightDark()
    {
        var markup = await RenderAsync<ThemePreferenceControl>();

        Assert.Contains("System", markup, StringComparison.Ordinal);
        Assert.Contains("Light", markup, StringComparison.Ordinal);
        Assert.Contains("Dark", markup, StringComparison.Ordinal);
        Assert.Contains("Preview action", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AboutSurface_ShowsVersionAndOnlyProvidedLinks()
    {
        var markup = await RenderComponentAsync<AboutSurface>(new Dictionary<string, object?>
        {
            [nameof(AboutSurface.AppVersion)] = "1.2.3",
            [nameof(AboutSurface.PrivacyHref)] = "/privacy",
            [nameof(AboutSurface.EducationHref)] = "/education"
        });

        Assert.Contains("1.2.3", markup, StringComparison.Ordinal);
        Assert.Contains("/privacy", markup, StringComparison.Ordinal);
        Assert.Contains("/education", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("/terms", markup, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync<TComponent>() where TComponent : IComponent
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<TComponent>(
                ParameterView.Empty);
            return component.ToHtmlString();
        });
    }

    private static async Task<string> RenderComponentAsync<TComponent>(IDictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<TComponent>(
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
