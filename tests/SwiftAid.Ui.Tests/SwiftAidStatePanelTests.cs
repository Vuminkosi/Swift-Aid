using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using SwiftAid.UI.Components.Theme;
using System.Text.RegularExpressions;

namespace SwiftAid.Ui.Tests;

public sealed class SwiftAidStatePanelTests
{
    [Fact]
    public async Task DeniedPanel_ExposesItsHeadingAsTheRegionName()
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);

        var markup = await RenderAsync(renderer, SwiftAidViewState.Denied, "Organization access unavailable");

        Assert.Contains("role=\"region\"", markup, StringComparison.Ordinal);
        Assert.Contains("Organization access unavailable", markup, StringComparison.Ordinal);
        AssertPanelHeadingIsReferenced(markup);
    }

    [Fact]
    public async Task LoadingPanel_AnnouncesItsStateAndEachPanelHasAnUniqueHeadingId()
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);

        var firstMarkup = await RenderAsync(renderer, SwiftAidViewState.Loading, "Loading assigned cases");
        var secondMarkup = await RenderAsync(renderer, SwiftAidViewState.Loading, "Loading follow-ups");

        Assert.Contains("role=\"status\"", firstMarkup, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", firstMarkup, StringComparison.Ordinal);
        AssertPanelHeadingIsReferenced(firstMarkup);
        AssertPanelHeadingIsReferenced(secondMarkup);
        Assert.NotEqual(GetHeadingId(firstMarkup), GetHeadingId(secondMarkup));
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

    private static Task<string> RenderAsync(
        HtmlRenderer renderer,
        SwiftAidViewState state,
        string title)
    {
        return renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<SwiftAidStatePanel>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(SwiftAidStatePanel.State)] = state,
                    [nameof(SwiftAidStatePanel.Title)] = title
                }));

            return component.ToHtmlString();
        });
    }

    private static void AssertPanelHeadingIsReferenced(string markup)
    {
        var headingId = GetHeadingId(markup);
        Assert.NotEmpty(headingId);
        Assert.Contains($"id=\"{headingId}\"", markup, StringComparison.Ordinal);
    }

    private static string GetHeadingId(string markup)
    {
        var match = Regex.Match(markup, "aria-labelledby=\\\"([^\\\"]+)\\\"");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
