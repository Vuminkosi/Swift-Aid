using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor;
using MudBlazor.Services;
using SwiftAid.UI.Components.Theme;

namespace SwiftAid.Ui.Tests;

public sealed class SwiftAidSettingsPrimitivesTests
{
    [Fact]
    public async Task Group_RendersLabelAndRows()
    {
        RenderFragment row = builder => RenderRow(builder, "Personal");
        var markup = await RenderAsync<SwiftAidSettingsGroup>(
            ("Label", (object)"Workspace"),
            ("HeadingId", (object)"group-heading"),
            ("ChildContent", (object)row));

        Assert.Contains("Workspace", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"group-heading\"", markup, StringComparison.Ordinal);
        Assert.Contains("Personal", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Row_WithHref_RendersLinkWithChevron()
    {
        var markup = await RenderAsync<SwiftAidSettingsRow>(
            ("Title", (object)"Security"),
            ("Href", (object)"/sign-in-methods"));

        Assert.Contains("href=\"/sign-in-methods\"", markup, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Security\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Row_WithActionText_ShowsLinkText()
    {
        var markup = await RenderAsync<SwiftAidSettingsRow>(
            ("Title", (object)"Security"),
            ("Href", (object)"/sign-in-methods"),
            ("ActionText", (object)"Manage"));

        Assert.Contains("Manage", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Row_WithTrailing_RendersContentWithoutLink()
    {
        RenderFragment trailing = builder =>
        {
            builder.OpenComponent<MudChip<string>>(0);
            builder.AddAttribute(1, "ChildContent", (RenderFragment)(b => b.AddContent(2, "Connected")));
            builder.CloseComponent();
        };
        var markup = await RenderAsync<SwiftAidSettingsRow>(
            ("Title", (object)"Google"),
            ("Trailing", (object)trailing));

        Assert.Contains("Connected", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<a", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Row_Disabled_RendersNoLink()
    {
        var markup = await RenderAsync<SwiftAidSettingsRow>(
            ("Title", (object)"Security"),
            ("Href", (object)"/sign-in-methods"),
            ("Disabled", (object)true));

        Assert.DoesNotContain("href=\"/sign-in-methods\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_RendersTitleMessageAndRetry()
    {
        var markup = await RenderAsync<SwiftAidSettingsStatus>(
            ("Title", (object)"Couldn't load workspaces."),
            ("Message", (object)"Try again when you are back online."),
            ("OnRetry", (object)EventCallback.Factory.Create(new object(), static () => { })));

        Assert.Contains("load workspaces", markup, StringComparison.Ordinal);
        Assert.Contains("Retry", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_WithoutHandler_RendersNoButton()
    {
        var markup = await RenderAsync<SwiftAidSettingsStatus>(
            ("Title", (object)"Couldn't load workspaces."),
            ("Message", (object)"Try again when you are back online."));

        Assert.Contains("Try again when you are back online.", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", markup, StringComparison.OrdinalIgnoreCase);
    }

    private static void RenderRow(RenderTreeBuilder builder, string title)
    {
        builder.OpenComponent<SwiftAidSettingsRow>(0);
        builder.AddAttribute(1, nameof(SwiftAidSettingsRow.Title), title);
        builder.CloseComponent();
    }

    private static async Task<string> RenderAsync<T>(params (string Name, object Value)[] parameters)
        where T : IComponent
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var pairs = parameters.ToDictionary(
                parameter => parameter.Name,
                parameter => (object?)parameter.Value);
            var component = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(pairs));
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
