using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using SwiftAid.UI.Components.Public;
using SwiftAid.UI.Components.Surfaces.Public;

namespace SwiftAid.Ui.Tests;

public sealed class WelcomeSurfaceFramingTests
{
    [Fact]
    public async Task WelcomeSurface_FramesChoiceAsPreferenceWithNoGrantDisclosure()
    {
        var markup = await RenderAsync();

        Assert.Contains("How will you use SwiftAid?", markup, StringComparison.Ordinal);
        Assert.Contains("does not grant access", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Choose your role", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WelcomeSurface_StillOffersAllNavigationIntents()
    {
        var markup = await RenderAsync();

        Assert.Contains("Community Health Worker", markup, StringComparison.Ordinal);
        Assert.Contains("Health Officer", markup, StringComparison.Ordinal);
        Assert.Contains("Public User", markup, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync()
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WelcomeSurface>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(WelcomeSurface.CountryOptions)] = Array.Empty<PublicCountryOption>(),
                    [nameof(WelcomeSurface.OnContinue)] = EventCallback.Factory.Create<WelcomeContinueRequest>(
                        new object(), (WelcomeContinueRequest _) => { })
                }));
            return component.ToHtmlString();
        });
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        return services.BuildServiceProvider();
    }

    private static HtmlRenderer CreateRenderer(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());
}
