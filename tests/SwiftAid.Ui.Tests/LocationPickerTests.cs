#pragma warning disable BL0005 // Logic tests set component parameters directly; production uses the renderer.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Geography;

namespace SwiftAid.Ui.Tests;

public sealed class LocationPickerTests
{
    private static readonly GeoPlaceOption Mbabane = new(
        "SZ:place:mbabane", "Mbabane", "Settlement", "SZ", "Eswatini", "Mbabane, Hhohho, Eswatini", true,
        [new GeoPlaceAncestor("country:SZ", "Eswatini", "Country"), new GeoPlaceAncestor("SZ:adm1:hhohho", "Hhohho", "Region")]);

    private static readonly GeoPlaceOption ManziniRegion = new(
        "SZ:adm1:manzini", "Manzini", "Region", "SZ", "Eswatini", "Manzini, Eswatini", true,
        [new GeoPlaceAncestor("country:SZ", "Eswatini", "Country")]);

    [Fact]
    public async Task Picker_renders_search_browse_and_provisional_entry()
    {
        var markup = await RenderHostAsync(DefaultParameters(), selected: null);

        Assert.Contains("Search for a place", markup, StringComparison.Ordinal);
        Assert.Contains("Browse by area", markup, StringComparison.Ordinal);
        Assert.Contains("Location not listed? Enter it manually", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Picker_shows_selected_ancestry_chip()
    {
        var markup = await RenderHostAsync(DefaultParameters(), Mbabane);

        Assert.Contains("Mbabane, Hhohho, Eswatini", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_returns_options_and_selects_through_callback()
    {
        GeoPlaceOption? chosen = null;
        var picker = LogicPicker(
            (_, _, _) => Task.FromResult(new GeoPlaceQueryResult(true, [Mbabane, ManziniRegion], null)),
            onSelected: option =>
            {
                chosen = option;
                return Task.CompletedTask;
            });

        var results = await picker.SearchPlacesAsync("man", CancellationToken.None);
        Assert.Equal(2, results.Count());
        await picker.SelectOptionAsync(results.First());

        Assert.Equal("SZ:place:mbabane", chosen?.Id);
    }

    [Fact]
    public async Task Search_short_query_returns_empty_without_calling_service()
    {
        var calls = 0;
        var picker = LogicPicker((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new GeoPlaceQueryResult(true, [Mbabane], null));
        });

        var results = await picker.SearchPlacesAsync("x", CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Search_failure_surfaces_retryable_copy_and_retry_recovers()
    {
        var shouldFail = true;
        var picker = LogicPicker((_, _, _) => Task.FromResult(shouldFail
            ? new GeoPlaceQueryResult(false, [], "Place search is temporarily unavailable.")
            : new GeoPlaceQueryResult(true, [Mbabane], null)));

        var results = await picker.SearchPlacesAsync("mbabane", CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal("Place search is temporarily unavailable.", picker.SearchError);

        shouldFail = false;
        await picker.RetrySearchAsync();

        Assert.Null(picker.SearchError);
    }

    [Fact]
    public async Task Cascade_drills_country_to_place_and_selects_deepest_level()
    {
        GeoPlaceOption? chosen = null;
        var picker = LogicPicker(
            (_, _, _) => Task.FromResult(new GeoPlaceQueryResult(true, [], null)),
            (parentId, _) => Task.FromResult(parentId switch
            {
                "country:SZ" => new GeoPlaceQueryResult(true, [ManziniRegion], null),
                "SZ:adm1:manzini" => new GeoPlaceQueryResult(true, [Mbabane], null),
                _ => new GeoPlaceQueryResult(true, [], null)
            }),
            onSelected: option =>
            {
                chosen = option;
                return Task.CompletedTask;
            });

        await picker.CascadeCountryChangedAsync("SZ");
        await picker.CascadeAreaChangedAsync("SZ:adm1:manzini");
        Assert.Equal("SZ:adm1:manzini", chosen?.Id);

        await picker.CascadePlaceChangedAsync("SZ:place:mbabane");
        Assert.Equal("SZ:place:mbabane", chosen?.Id);
    }

    [Fact]
    public async Task Provisional_request_reaches_parent()
    {
        var requested = false;
        var picker = LogicPicker(
            (_, _, _) => Task.FromResult(new GeoPlaceQueryResult(true, [], null)),
            onProvisional: () =>
            {
                requested = true;
                return Task.CompletedTask;
            });

        await picker.RequestProvisionalAsync();

        Assert.True(requested);
    }

    private static SwiftAidLocationPicker LogicPicker(
        Func<string, string?, CancellationToken, Task<GeoPlaceQueryResult>> search,
        Func<string, CancellationToken, Task<GeoPlaceQueryResult>>? children = null,
        Func<GeoPlaceOption?, Task>? onSelected = null,
        Func<Task>? onProvisional = null)
    {
        var receiver = new object();
        return new SwiftAidLocationPicker
        {
            SearchFunc = search,
            ChildrenFunc = children
                ?? ((_, _) => Task.FromResult(new GeoPlaceQueryResult(true, [], null))),
            Countries = [new GeoPlaceCountry("SZ", "Eswatini")],
            SelectedChanged = onSelected is null
                ? EventCallback<GeoPlaceOption?>.Empty
                : EventCallback.Factory.Create<GeoPlaceOption?>(receiver, onSelected),
            OnProvisionalRequested = onProvisional is null
                ? EventCallback.Empty
                : EventCallback.Factory.Create(receiver, onProvisional)
        };
    }

    private static Dictionary<string, object?> DefaultParameters() => new()
    {
        [nameof(SwiftAidLocationPicker.SearchFunc)] =
            (Func<string, string?, CancellationToken, Task<GeoPlaceQueryResult>>)((_, _, _) =>
                Task.FromResult(new GeoPlaceQueryResult(true, [], null))),
        [nameof(SwiftAidLocationPicker.ChildrenFunc)] =
            (Func<string, CancellationToken, Task<GeoPlaceQueryResult>>)((_, _) =>
                Task.FromResult(new GeoPlaceQueryResult(true, [], null))),
        [nameof(SwiftAidLocationPicker.Countries)] =
            (IReadOnlyList<GeoPlaceCountry>)[new GeoPlaceCountry("SZ", "Eswatini")],
        [nameof(SwiftAidLocationPicker.OnProvisionalRequested)] =
            EventCallback.Factory.Create(new object(), () => Task.CompletedTask)
    };

    private static async Task<string> RenderHostAsync(Dictionary<string, object?> parameters, GeoPlaceOption? selected)
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            parameters[nameof(SwiftAidLocationPicker.Selected)] = selected;
            var root = await renderer.RenderComponentAsync<PickerHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(PickerHost.PickerParameters)] = parameters
                }));
            return root.ToHtmlString();
        });
    }

    private sealed class PickerHost : ComponentBase
    {
        [Parameter] public Dictionary<string, object?> PickerParameters { get; set; } = new();

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<SwiftAidLocationPicker>(0);
            builder.AddMultipleAttributes(1, PickerParameters
                .Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value!)));
            builder.CloseComponent();
        }
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
