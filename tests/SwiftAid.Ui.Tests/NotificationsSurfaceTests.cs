using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using SwiftAid.UI.Components.Surfaces.Account;

namespace SwiftAid.Ui.Tests;

public sealed class NotificationsSurfaceTests
{
    [Fact]
    public async Task NotificationsSurface_SignedOutOffersSignIn()
    {
        var markup = await RenderAsync(NotificationsSnapshot.SignedOut);

        Assert.Contains("Sign in to view notification preferences.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificationsSurface_ReadyShowsTogglesAndSave()
    {
        var markup = await RenderAsync(new NotificationsSnapshot(
            NotificationsViewState.Ready,
            WorkReminders: false,
            SessionUpdates: true,
            IsSaving: false));

        Assert.Contains("Work reminders", markup, StringComparison.Ordinal);
        Assert.Contains("Session updates", markup, StringComparison.Ordinal);
        Assert.Contains("Save preferences", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificationsSurface_ReadyShowsHonestSubscriptionsEntry()
    {
        var markup = await RenderAsync(
            new NotificationsSnapshot(
                NotificationsViewState.Ready,
                WorkReminders: true,
                SessionUpdates: true,
                IsSaving: false),
            sessionsHref: "/sessions");

        Assert.Contains("not available in this service yet", markup, StringComparison.Ordinal);
        Assert.Contains("/sessions", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificationsSurface_OmitsSessionsLinkWhenHostHasNoRoute()
    {
        var markup = await RenderAsync(
            new NotificationsSnapshot(
                NotificationsViewState.Ready,
                WorkReminders: true,
                SessionUpdates: true,
                IsSaving: false),
            sessionsHref: null);

        Assert.Contains("not available in this service yet", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("/sessions", markup, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(NotificationsSnapshot snapshot, string? sessionsHref = null)
    {
        await using var services = CreateServices();
        await using var renderer = CreateRenderer(services);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<NotificationsSurface>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(NotificationsSurface.Snapshot)] = snapshot,
                    [nameof(NotificationsSurface.SessionsHref)] = sessionsHref,
                    [nameof(NotificationsSurface.OnRefresh)] = EventCallback.Factory.Create(
                        new object(), () => { }),
                    [nameof(NotificationsSurface.OnSave)] = EventCallback.Factory.Create<NotificationPreferenceChange>(
                        new object(), (NotificationPreferenceChange _) => { })
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
