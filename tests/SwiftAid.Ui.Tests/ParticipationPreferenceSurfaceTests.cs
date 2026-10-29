using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Surfaces.Account;

namespace SwiftAid.Ui.Tests;

public sealed class ParticipationPreferenceSurfaceTests
{
    [Fact]
    public async Task ReadyShowsFiveOptionsAndPrivilegeGuard()
    {
        var markup = await RenderAsync(Ready(), organizationsHref: "/organizations");

        Assert.Contains("Community Health Worker", markup, StringComparison.Ordinal);
        Assert.Contains("Nurse / Clinician", markup, StringComparison.Ordinal);
        Assert.Contains("Community Leader", markup, StringComparison.Ordinal);
        Assert.Contains("Program Coordinator", markup, StringComparison.Ordinal);
        Assert.Contains("Community Member", markup, StringComparison.Ordinal);
        Assert.Contains("never grants clinical", markup, StringComparison.Ordinal);
        Assert.Contains("Stored on this device only", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadyWithActiveMembershipShowsAuthorizedAccessBanner()
    {
        var markup = await RenderAsync(
            Ready() with { HasActiveMembership = true },
            organizationsHref: "/organizations");

        Assert.Contains("authorized access through an approved membership", markup, StringComparison.Ordinal);
        Assert.Contains("View your access", markup, StringComparison.Ordinal);
        Assert.Contains("/organizations", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadyWithPendingRequestShowsPendingCopy()
    {
        var markup = await RenderAsync(
            Ready() with { HasPendingRequest = true },
            organizationsHref: "/organizations");

        Assert.Contains("still pending", markup, StringComparison.Ordinal);
        Assert.Contains("View request status", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadyWithoutOrganizationsHrefOmitsMembershipLinks()
    {
        var markup = await RenderAsync(Ready(), organizationsHref: null);

        Assert.Contains("never grants clinical", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("/organizations", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedOutPromptsSignIn()
    {
        var markup = await RenderAsync(
            ParticipationPreferenceSnapshot.SignedOut,
            organizationsHref: "/organizations");

        Assert.Contains("Sign in to choose how you participate.", markup, StringComparison.Ordinal);
    }

    private static ParticipationPreferenceSnapshot Ready() => new(
        ParticipationPreferenceViewState.Ready,
        SelectedKey: null,
        HasActiveMembership: false,
        HasPendingRequest: false);

    private static async Task<string> RenderAsync(
        ParticipationPreferenceSnapshot snapshot,
        string? organizationsHref)
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ParticipationPreferenceSurface>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(ParticipationPreferenceSurface.Snapshot)] = snapshot,
                    [nameof(ParticipationPreferenceSurface.OrganizationsHref)] = organizationsHref,
                    [nameof(ParticipationPreferenceSurface.OnRefresh)] = EventCallback.Factory.Create(
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
