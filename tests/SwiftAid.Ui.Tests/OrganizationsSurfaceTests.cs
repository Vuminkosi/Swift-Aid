using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Surfaces.Account;

namespace SwiftAid.Ui.Tests;

public sealed class OrganizationsSurfaceTests
{
    [Fact]
    public async Task OrganizationsSurface_SignedOutOffersSignIn()
    {
        var markup = await RenderAsync(OrganizationsSnapshot.SignedOut);

        Assert.Contains("Sign in to view your workspaces.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrganizationsSurface_ReadyShowsMembershipsDirectoryAndRequests()
    {
        var markup = await RenderAsync(new OrganizationsSnapshot(
            OrganizationsViewState.Ready,
            [new MembershipSnapshot("demo-community-clinic", "Active", "Active", ["main-clinic"])],
            [new MembershipRequestSnapshot("req-1", "demo-community-clinic", "Pending", "Field work")],
            [new OrganizationOptionSnapshot("demo-community-clinic", "Active")],
            IsSaving: false));

        Assert.Contains("Your workspaces", markup, StringComparison.Ordinal);
        Assert.Contains("demo-community-clinic", markup, StringComparison.Ordinal);
        Assert.Contains("Join an organization", markup, StringComparison.Ordinal);
        Assert.Contains("Your requests", markup, StringComparison.Ordinal);
        Assert.Contains("Request access", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrganizationsSurface_EmptyMembershipsExplainsNextStep()
    {
        var markup = await RenderAsync(new OrganizationsSnapshot(
            OrganizationsViewState.Ready, [], [], [], IsSaving: false));

        Assert.Contains("No workspaces yet.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrganizationsSurface_ReadyOffersInvitationCodeRedemption()
    {
        var markup = await RenderAsync(new OrganizationsSnapshot(
            OrganizationsViewState.Ready, [], [], [], IsSaving: false));

        Assert.Contains("invitation code", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redeem code", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadyActionsMeetTouchTargetClass()
    {
        var markup = await RenderAsync(new OrganizationsSnapshot(
            OrganizationsViewState.Ready, [], [], [], IsSaving: false));

        Assert.Contains("auth-cta", markup, StringComparison.Ordinal);
        Assert.Contains("Enter invitation code", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaffSeesIssuanceSection()
    {
        var markup = await RenderAsync(new OrganizationsSnapshot(
            OrganizationsViewState.Ready,
            [new MembershipSnapshot("org-1", "Active", "Active", ["ws-1"])],
            [],
            [],
            IsSaving: false));

        Assert.Contains("Issue invitation codes", markup, StringComparison.Ordinal);
        Assert.Contains("Create invitation code", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainMemberSeesNoIssuanceSection()
    {
        var markup = await RenderAsync(new OrganizationsSnapshot(
            OrganizationsViewState.Ready,
            [new MembershipSnapshot("org-1", "Active", "Active", [])],
            [],
            [],
            IsSaving: false));

        Assert.DoesNotContain("Issue invitation codes", markup, StringComparison.Ordinal);
        Assert.Contains("Enter invitation code", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssuedCodeRendersOnce()
    {
        var snapshot = new OrganizationsSnapshot(
            OrganizationsViewState.Ready,
            [new MembershipSnapshot("org-1", "Active", "Active", ["ws-1"])],
            [],
            [],
            IsSaving: false,
            IssuedInvitation: new OrganizationInvitationIssue("ABCD-1234-EFGH", "org-1", null));
        var markup = await RenderAsync(snapshot);

        Assert.Contains("ABCD-1234-EFGH", markup, StringComparison.Ordinal);
        Assert.Contains("shown only once", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderFaultShowsRecovery()
    {
        var markup = await RenderAsync(null!);

        Assert.Contains("failed to load", markup, StringComparison.Ordinal);
        Assert.Contains("Retry", markup, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(OrganizationsSnapshot snapshot)
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<OrganizationsSurface>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(OrganizationsSurface.Snapshot)] = snapshot,
                    [nameof(OrganizationsSurface.OnRefresh)] = EventCallback.Factory.Create(
                        new object(), () => { }),
                    [nameof(OrganizationsSurface.OnSearch)] = EventCallback.Factory.Create<string?>(
                        new object(), (string? _) => { }),
                    [nameof(OrganizationsSurface.OnRequest)] = EventCallback.Factory.Create<MembershipJoinRequest>(
                        new object(), (MembershipJoinRequest _) => { })
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
        services.AddSingleton<IErrorBoundaryLogger>(NoOpErrorBoundaryLogger.Instance);
        return services.BuildServiceProvider();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            Uri = ToAbsoluteUri(uri).ToString();
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

    private sealed class NoOpErrorBoundaryLogger : IErrorBoundaryLogger
    {
        public static readonly NoOpErrorBoundaryLogger Instance = new();

        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }

    private static HtmlRenderer CreateRenderer(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());
}
