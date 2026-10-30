using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.Contracts.Cases;
using SwiftAid.UI.Components.Surfaces.Public;

namespace SwiftAid.Ui.Tests;

public sealed class PublicHomeStaffWorkspaceEntryTests
{
    [Fact]
    public async Task GuestWithWorkspaceHref_RendersOneAccessibleWorkspaceLink()
    {
        var markup = await RenderAsync(isSignedIn: false, staffWorkspaceHref: "/dashboard");

        var link = Assert.Single(GetLinks(markup, "Staff workspace"));
        Assert.Equal("/dashboard", link.Groups[1].Value);
    }

    [Fact]
    public async Task GuestWithoutWorkspaceHref_DoesNotRenderWorkspaceLink()
    {
        var markup = await RenderAsync(isSignedIn: false, staffWorkspaceHref: null);

        Assert.Empty(GetLinks(markup, "Staff workspace"));
    }

    [Fact]
    public async Task SignedInSurface_DoesNotRenderGuestWorkspaceEntry()
    {
        var markup = await RenderAsync(isSignedIn: true, staffWorkspaceHref: "/dashboard");

        Assert.Empty(GetLinks(markup, "Staff workspace"));
    }

    private static MatchCollection GetLinks(string markup, string label)
    {
        var anchors = Regex.Matches(markup,
            "<a\\b[^>]*\\bhref=\\\"([^\\\"]*)\\\"[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return Regex.Matches(string.Join("\n", anchors.Cast<Match>()
            .Where(anchor => Regex.Replace(anchor.Groups[2].Value, "<[^>]+>", string.Empty)
                .Contains(label, StringComparison.Ordinal))
            .Select(anchor => anchor.Value)), "<a\\b[^>]*\\bhref=\\\"([^\\\"]*)\\\"[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static async Task<string> RenderAsync(bool isSignedIn, string? staffWorkspaceHref)
    {
        await using var services = CreateServices();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<PublicHomeSurface>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(PublicHomeSurface.IsSignedIn)] = isSignedIn,
                    [nameof(PublicHomeSurface.CaseQueueClient)] = new EmptyCaseQueueClient(),
                    [nameof(PublicHomeSurface.StaffWorkspaceHref)] = staffWorkspaceHref
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

        protected override void BeginInvokeJS(long taskId, string identifier, string? argsJson,
            JSCallResultType resultType, long targetInstanceId)
        {
        }

        protected override void EndInvokeDotNet(DotNetInvocationInfo callInfo, in DotNetInvocationResult result)
        {
        }
    }

    private sealed class EmptyCaseQueueClient : ICaseQueueClient
    {
        public Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, Array.Empty<CaseQueueItem>()));
    }
}
