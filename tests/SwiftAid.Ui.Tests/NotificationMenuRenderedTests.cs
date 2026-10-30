using Microsoft.AspNetCore.Components;
using System.Reflection;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using SwiftAid.Client.Notifications;
using SwiftAid.Contracts.Notifications;
using SwiftAid.Gateway.Components.Layout;
using SwiftAid.UI.Components.Notifications;

namespace SwiftAid.Ui.Tests;

#pragma warning disable BL0006 // The lightweight renderer inspects real component parameters and callbacks.
public sealed class NotificationMenuRenderedTests
{
    [Fact]
    public async Task OpenLoadsAuthorizedFeedAndActionsUseClientAndSafeCaseRoute()
    {
        var notificationId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var item = new NotificationItemView(
            notificationId,
            NotificationKind.CaseNeedsAttention,
            DateTimeOffset.UtcNow,
            null,
            null,
            NotificationAction.OpenAssignedWork,
            caseId.ToString("D"));
        var client = new FakeNotificationFeedClient(item);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
        var navigation = new TestNavigationManager();
        services.AddSingleton<NavigationManager>(navigation);
        services.AddSingleton<INotificationFeedClient>(client);
        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new TestRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());

        var host = await renderer.RenderAsync<NotificationMenuHost>();
        try
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                Assert.Equal(0, client.GetCalls);
                Assert.Contains("Notifications", renderer.GetRenderedAttributeValues(host.ComponentId, typeof(MudIconButton), "aria-label"));

                var menuCallback = (EventCallback<bool>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudMenu),
                    nameof(MudMenu.OpenChanged))!;
                await menuCallback.InvokeAsync(true);

                Assert.Equal(1, client.GetCalls);
                Assert.Contains("Notifications, 1 unread", renderer.GetRenderedAttributeValues(host.ComponentId, typeof(MudIconButton), "aria-label"));
                Assert.Contains("true", renderer.GetRenderedAttributeValues(host.ComponentId, typeof(MudIconButton), "aria-expanded"));

                var feedItems = (IReadOnlyList<NotificationItemView>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(NotificationFeed),
                    nameof(NotificationFeed.Items))!;
                Assert.Single(feedItems);
                var markRead = (Func<Guid, Task<bool>>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(NotificationFeed),
                    nameof(NotificationFeed.OnMarkRead))!;
                var dismiss = (Func<Guid, Task<bool>>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(NotificationFeed),
                    nameof(NotificationFeed.OnDismiss))!;
                var open = (EventCallback<NotificationItemView>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(NotificationFeed),
                    nameof(NotificationFeed.OnOpen))!;

                Assert.True(await markRead(notificationId));
                Assert.Equal(notificationId, client.LastMarkedReadId);
                Assert.True(await dismiss(notificationId));
                Assert.Equal(notificationId, client.LastDismissedId);
                await open.InvokeAsync(item);
                Assert.Equal($"http://localhost/case/{caseId:D}", navigation.Uri);
            });
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task InvalidOpaqueReferenceDoesNotNavigate()
    {
        var client = new FakeNotificationFeedClient(new NotificationItemView(
            Guid.NewGuid(), NotificationKind.CaseNeedsAttention, DateTimeOffset.UtcNow,
            null, null, NotificationAction.OpenAssignedWork, "//external.example/path"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
        var navigation = new TestNavigationManager();
        services.AddSingleton<NavigationManager>(navigation);
        services.AddSingleton<INotificationFeedClient>(client);
        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new TestRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());
        var host = await renderer.RenderAsync<NotificationMenuHost>();
        try
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var menuCallback = (EventCallback<bool>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudMenu),
                    nameof(MudMenu.OpenChanged))!;
                await menuCallback.InvokeAsync(true);
                var visibleItem = ((IReadOnlyList<NotificationItemView>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(NotificationFeed),
                    nameof(NotificationFeed.Items))!)[0];
                Assert.Equal(NotificationAction.OpenAssignedWork, visibleItem.Action);
                Assert.Contains("status", renderer.GetRenderedAttributeValues(host.ComponentId, typeof(MudText), "role"));
                Assert.DoesNotContain("Open notification", renderer.GetRenderedAttributeValues(host.ComponentId, typeof(MudButton), "aria-label"));
                Assert.Equal("http://localhost/", navigation.Uri);
                Assert.Null(client.LastMarkedReadId);
            });
        }
        finally
        {
            host.Dispose();
        }
    }

#pragma warning disable BL0005 // This test exercises the component's parameter-driven ref pruning directly.
    [Fact]
    public void ConditionalActionReferencesArePrunedAsEligibilityChanges()
    {
        var itemId = Guid.NewGuid();
        var item = new NotificationItemView(
            itemId, NotificationKind.CaseNeedsAttention, DateTimeOffset.UtcNow,
            null, null, NotificationAction.OpenAssignedWork, Guid.NewGuid().ToString("D"));
        var feed = new NotificationFeed
        {
            Items = [item],
            OnOpen = EventCallback.Factory.Create<NotificationItemView>(new object(), _ => Task.CompletedTask)
        };
        var openButtons = GetReferenceMap<MudButton>(feed, "_openActionButtons");
        var markReadButtons = GetReferenceMap<MudButton>(feed, "_markReadActionButtons");
        var dismissButtons = GetReferenceMap<MudIconButton>(feed, "_dismissActionButtons");
        openButtons[itemId] = new MudButton();
        markReadButtons[itemId] = new MudButton();
        dismissButtons[itemId] = new MudIconButton();

        InvokeOnParametersSet(feed);
        Assert.Single(openButtons);
        Assert.Single(markReadButtons);
        Assert.Single(dismissButtons);

        feed.Items = [item with { ReadAtUtc = DateTimeOffset.UtcNow }];
        InvokeOnParametersSet(feed);
        Assert.Single(openButtons);
        Assert.Empty(markReadButtons);
        Assert.Single(dismissButtons);

        feed.OnOpen = default;
        InvokeOnParametersSet(feed);
        Assert.Empty(openButtons);
        Assert.Single(dismissButtons);

        feed.Items = [item with { DismissedAtUtc = DateTimeOffset.UtcNow }];
        InvokeOnParametersSet(feed);
        Assert.Empty(openButtons);
        Assert.Empty(markReadButtons);
        Assert.Empty(dismissButtons);
    }

    private static Dictionary<Guid, TComponent> GetReferenceMap<TComponent>(NotificationFeed feed, string fieldName)
        where TComponent : class =>
        (Dictionary<Guid, TComponent>)typeof(NotificationFeed)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(feed)!;

    private static void InvokeOnParametersSet(NotificationFeed feed) =>
        typeof(NotificationFeed).GetMethod("OnParametersSet", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(feed, null);
#pragma warning restore BL0005

    [Fact]
    public async Task PermissionDeniedFeedDoesNotRenderFixtureNotifications()
    {
        var client = new FakeNotificationFeedClient(NotificationFeedResult.PermissionDenied());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        services.AddSingleton<INotificationFeedClient>(client);
        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new TestRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());
        var host = await renderer.RenderAsync<NotificationMenuHost>();
        try
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var menuCallback = (EventCallback<bool>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudMenu),
                    nameof(MudMenu.OpenChanged))!;
                await menuCallback.InvokeAsync(true);
                Assert.Empty((IReadOnlyList<NotificationItemView>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(NotificationFeed),
                    nameof(NotificationFeed.Items))!);
                Assert.True((bool)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(NotificationFeed),
                    nameof(NotificationFeed.PermissionDenied))!);
            });
        }
        finally
        {
            host.Dispose();
        }
    }

    private sealed class NotificationMenuHost : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<NotificationMenu>(1);
            builder.CloseComponent();
        }
    }

    private sealed class TestRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public async Task<RenderedComponent<TComponent>> RenderAsync<TComponent>() where TComponent : IComponent
        {
            var component = (TComponent)InstantiateComponent(typeof(TComponent));
            var componentId = -1;
            await Dispatcher.InvokeAsync(async () =>
            {
                componentId = AssignRootComponentId(component);
                await RenderRootComponentAsync(componentId);
            });
            return new RenderedComponent<TComponent>(this, componentId);
        }

        public object? GetComponentParameter(int rootId, Type componentType, string name)
        {
            var frames = GetCurrentRenderTreeFrames(rootId);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Component)
                {
                    continue;
                }

                if (componentType.IsAssignableFrom(frame.ComponentType))
                {
                    for (var attributeIndex = index + 1; attributeIndex < frames.Count; attributeIndex++)
                    {
                        var attribute = frames.Array[attributeIndex];
                        if (attribute.FrameType != RenderTreeFrameType.Attribute)
                        {
                            break;
                        }

                        if (attribute.AttributeName == name)
                        {
                            return attribute.AttributeValue;
                        }
                    }
                }

                var nestedValue = GetComponentParameter(frame.ComponentId, componentType, name);
                if (nestedValue is not null)
                {
                    return nestedValue;
                }
            }

            return null;
        }

        public string? GetRenderedAttribute(int rootId, Type componentType, string attributeName)
        {
            foreach (var componentId in FindComponentIds(rootId, componentType))
            {
                var frames = GetCurrentRenderTreeFrames(componentId);
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames.Array[index];
                    if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == attributeName)
                    {
                        return frame.AttributeValue?.ToString();
                    }
                }
            }

            return null;
        }

        public IReadOnlyList<string?> GetRenderedAttributeValues(int rootId, Type componentType, string attributeName)
        {
            var values = new List<string?>();
            foreach (var componentId in FindComponentIds(rootId, componentType))
            {
                var frames = GetCurrentRenderTreeFrames(componentId);
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames.Array[index];
                    if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == attributeName)
                    {
                        values.Add(frame.AttributeValue?.ToString());
                    }
                }
            }

            return values;
        }

        private IEnumerable<int> FindComponentIds(int rootId, Type componentType)
        {
            var frames = GetCurrentRenderTreeFrames(rootId);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Component)
                {
                    continue;
                }

                if (componentType.IsAssignableFrom(frame.ComponentType))
                {
                    yield return frame.ComponentId;
                }

                foreach (var child in FindComponentIds(frame.ComponentId, componentType))
                {
                    yield return child;
                }
            }
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
        public void Unmount(int componentId) => RemoveRootComponent(componentId);
    }

    private sealed class RenderedComponent<TComponent>(TestRenderer renderer, int componentId) : IDisposable
        where TComponent : IComponent
    {
        public int ComponentId { get; } = componentId;
        public void Dispose() => renderer.Dispatcher.InvokeAsync(() => renderer.Unmount(ComponentId)).GetAwaiter().GetResult();
    }

    private sealed class FakeNotificationFeedClient : INotificationFeedClient
    {
        private readonly List<NotificationItemView> _items;
        private readonly NotificationFeedStatus _status;
        public IReadOnlyList<NotificationItemView> Items => _items;
        public int GetCalls { get; private set; }
        public Guid? LastMarkedReadId { get; private set; }
        public Guid? LastDismissedId { get; private set; }

        public FakeNotificationFeedClient(NotificationItemView item) : this(NotificationFeedResult.Ready([item])) { }

        public FakeNotificationFeedClient(NotificationFeedResult result)
        {
            _items = result.Items.ToList();
            _status = result.Status;
        }

        public Task<NotificationFeedResult> GetAsync(CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(new NotificationFeedResult(_status, _items));
        }

        public Task<bool> MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default)
        {
            LastMarkedReadId = notificationId;
            var index = _items.FindIndex(item => item.Id == notificationId);
            if (index >= 0)
            {
                _items[index] = _items[index] with { ReadAtUtc = DateTimeOffset.UtcNow };
            }
            return Task.FromResult(true);
        }

        public Task<bool> DismissAsync(Guid notificationId, CancellationToken cancellationToken = default)
        {
            LastDismissedId = notificationId;
            _items.RemoveAll(item => item.Id == notificationId);
            return Task.FromResult(true);
        }

        public Task<PushDeviceRegistrationResult> RegisterDeviceAsync(Guid installationId, PushDevicePlatform platform, string token, bool soundEnabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PushDeviceRegistrationResult(PushDeviceRegistrationStatus.Unavailable));

        public Task<PushDeviceRemovalResult> RemoveDeviceAsync(Guid installationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PushDeviceRemovalResult(PushDeviceRemovalStatus.Unavailable));

        public async IAsyncEnumerable<bool> WatchChangesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task MonitorChangesAsync(Func<CancellationToken, Task> refreshAuthorizedFeedAsync, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
#pragma warning restore BL0006
