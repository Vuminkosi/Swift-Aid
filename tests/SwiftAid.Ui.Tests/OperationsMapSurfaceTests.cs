using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Surfaces.Operations;

namespace SwiftAid.Ui.Tests;

/// <summary>
/// Static-render coverage for the shared surveillance map surface. The desktop
/// table and mobile list branches require viewport JavaScript, so they are not
/// asserted here; row content is pinned by <c>MapAggregateContractTests</c>.
/// </summary>
public sealed class OperationsMapSurfaceTests
{
    [Fact]
    public async Task UnavailableState_ExplainsMapDataIsNotAvailable()
    {
        var markup = await RenderAsync(
            OperationsSurfaceState.Unavailable,
            Array.Empty<OperationsMapSurface.AreaAggregateView>());

        Assert.Contains("Map data is not available", markup, StringComparison.Ordinal);
        Assert.Contains("Map totals are not available right now.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeniedState_ExplainsAccessIsMissing()
    {
        var markup = await RenderAsync(
            OperationsSurfaceState.Denied,
            Array.Empty<OperationsMapSurface.AreaAggregateView>());

        Assert.Contains("You do not have access to this information", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PopulatedWithoutAreas_ReportsNoAreaTotals()
    {
        var markup = await RenderAsync(
            OperationsSurfaceState.Populated,
            Array.Empty<OperationsMapSurface.AreaAggregateView>());

        Assert.Contains("No area totals", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PopulatedWithAreas_RendersHeadingsTableFallbackAndFreshness()
    {
        var areas = new[]
        {
            new OperationsMapSurface.AreaAggregateView(
                "SZ",
                "Eswatini",
                OperationsMapSurface.AggregatePrecision.Country,
                12,
                3,
                15,
                false,
                null,
                new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero))
        };

        var markup = await RenderAsync(OperationsSurfaceState.Populated, areas, updatedAtUtc: true);

        Assert.Contains("Area totals", markup, StringComparison.Ordinal);
        Assert.Contains("Country totals", markup, StringComparison.Ordinal);
        Assert.Contains("Area totals remain available in the table.", markup, StringComparison.Ordinal);
        Assert.Contains("Updated", markup, StringComparison.Ordinal);
        Assert.Contains("the last 30 days", markup, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(
        OperationsSurfaceState state,
        IReadOnlyList<OperationsMapSurface.AreaAggregateView> areas,
        bool updatedAtUtc = false)
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = new Dictionary<string, object?>
            {
                [nameof(OperationsMapSurface.Title)] = "Regional activity",
                [nameof(OperationsMapSurface.Description)] = "Review country and region totals by report group.",
                [nameof(OperationsMapSurface.State)] = state,
                [nameof(OperationsMapSurface.Areas)] = areas,
                [nameof(OperationsMapSurface.PeriodLabel)] = "the last 30 days"
            };
            if (updatedAtUtc)
            {
                parameters[nameof(OperationsMapSurface.UpdatedAtUtc)] =
                    new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
            }

            var component = await renderer.RenderComponentAsync<OperationsMapSurface>(
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

    private static HtmlRenderer CreateRenderer(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());

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
}
