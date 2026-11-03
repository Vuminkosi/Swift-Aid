using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using System.Reflection;
using SwiftAid.Client.Presentation;
using SwiftAid.UI.Components.Surfaces.Account;

namespace SwiftAid.Ui.Tests;

public sealed class NotificationsSurfaceTests
{
    [Fact]
    public async Task GatewayPreferenceExceptionBecomesAnUnavailableState()
    {
        using var services = new ServiceCollection()
            .AddSingleton<INotificationPreferenceClient>(new ThrowingPreferenceClient())
            .BuildServiceProvider();
        var page = new SwiftAid.Gateway.Components.Pages.Notifications();
        var type = page.GetType();
        type.GetProperty("ServiceProvider", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, services);
        await (Task)type.GetMethod("RefreshPreferencesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null)!;
        var snapshot = (NotificationsSnapshot)type.GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        Assert.Equal(NotificationsViewState.Unavailable, snapshot.ViewState);
    }

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
    public async Task NotificationsSurface_ShowsOwnedSessionSubscriptionAndUnsubscribeAction()
    {
        var sessionId = Guid.NewGuid();
        var markup = await RenderAsync(
            new NotificationsSnapshot(
                NotificationsViewState.Ready,
                WorkReminders: true,
                SessionUpdates: true,
                IsSaving: false),
            sessionsHref: "/sessions",
            subscriptions: [new SessionSubscriptionSummary(
                sessionId, 3, "Community workshop", DateTimeOffset.UtcNow.AddDays(2),
                "Africa/Mbabane", "Email reminders")]);

        Assert.Contains("Community workshop", markup, StringComparison.Ordinal);
        Assert.Contains("Africa/Mbabane", markup, StringComparison.Ordinal);
        Assert.Contains("Email reminders", markup, StringComparison.Ordinal);
        Assert.Contains($"/sessions?sessionId={sessionId:D}", markup, StringComparison.Ordinal);
        Assert.Contains("Unsubscribe", markup, StringComparison.Ordinal);
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
            sessionsHref: null,
            subscriptions: []);

        Assert.Contains("no active session subscriptions", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("/sessions", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificationsSurface_ShowsSubscriptionsWhenPreferencesAreUnavailable()
    {
        var markup = await RenderAsync(NotificationsSnapshot.Unavailable, "/sessions",
            [new SessionSubscriptionSummary(Guid.NewGuid(), 2, "Community workshop", null, null, "Email reminders")]);

        Assert.Contains("temporarily unavailable", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Community workshop", markup, StringComparison.Ordinal);
        Assert.Contains("Unsubscribe", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificationsSurface_DoesNotDisplaySubscriptionsInSignedOutState()
    {
        var markup = await RenderAsync(NotificationsSnapshot.SignedOut, "/sessions",
            [new SessionSubscriptionSummary(Guid.NewGuid(), 2, "Private session label", null, null, null)]);

        Assert.DoesNotContain("Private session label", markup, StringComparison.Ordinal);
        Assert.Contains("Sign in", markup, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(
        NotificationsSnapshot snapshot,
        string? sessionsHref = null,
        IReadOnlyList<SessionSubscriptionSummary>? subscriptions = null)
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
                    [nameof(NotificationsSurface.Subscriptions)] = subscriptions,
                    [nameof(NotificationsSurface.OnUnsubscribe)] = EventCallback.Factory.Create<SessionSubscriptionSummary>(
                        new object(), (SessionSubscriptionSummary _) => { }),
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

    private sealed class ThrowingPreferenceClient : INotificationPreferenceClient
    {
        public Task<UiCapabilityResult<NotificationPreferenceState>> GetAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("unavailable");
        public Task<UiCapabilityResult<NotificationPreferenceState>> SaveAsync(NotificationPreferenceEdit edit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
