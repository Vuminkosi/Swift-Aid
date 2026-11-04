using System.Reflection;
using Bit.Bonboarding;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Components.Layout;
using SwiftAid.UI.Components.Surfaces.Operations;
using SwiftAid.UI.Ellai;
using SwiftAid.UI.Ellai.Components;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

#pragma warning disable BL0006 // Render-tree access is required to exercise the real component event callback.
public sealed class AuthorizedLayoutNavigationAccessibilityTests
{
    private const string NavigationDrawerId = "swiftaid-authorized-navigation";

    [Fact]
    public async Task CompactCaseFiltersToggleUpdatesExpandedStateWithoutChangingQuery()
    {
        await using var services = CreateServices(Breakpoint.Xs, out _);
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var host = await renderer.RenderAsync<OperationsCaseQueue>();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var originalQuery = host.Component.Query;
            Assert.DoesNotContain("--expanded", renderer.GetElementAttribute(host.ComponentId, "case-queue-advanced-filters", "class"));
            Assert.Equal("case-queue-advanced-filters",
                renderer.GetRenderedAttribute(host.ComponentId, typeof(MudButton), "aria-controls"));
            Assert.Equal("false",
                renderer.GetRenderedAttribute(host.ComponentId, typeof(MudButton), "aria-expanded"));
            var toggle = (EventCallback<MouseEventArgs>)renderer.GetComponentParameter(
                host.ComponentId, typeof(MudButton), nameof(MudButton.OnClick))!;
            await toggle.InvokeAsync(new MouseEventArgs());
            Assert.Contains("--expanded", renderer.GetElementAttribute(host.ComponentId, "case-queue-advanced-filters", "class"));
            Assert.Equal("true",
                renderer.GetRenderedAttribute(host.ComponentId, typeof(MudButton), "aria-expanded"));
            await toggle.InvokeAsync(new MouseEventArgs());
            Assert.DoesNotContain("--expanded", renderer.GetElementAttribute(host.ComponentId, "case-queue-advanced-filters", "class"));
            Assert.Equal(originalQuery, host.Component.Query);
            Assert.Equal("false",
                renderer.GetRenderedAttribute(host.ComponentId, typeof(MudButton), "aria-expanded"));
        });
        host.Dispose();
    }

    [Fact]
    public async Task Menu_RendersTrustedRoutesAndMatchingSemanticAnchors()
    {
        await using var services = CreateServices(Breakpoint.Md, out _);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<SwiftAidNavMenu>();
            return rendered.ToHtmlString();
        });

        foreach (var destination in EllaiNavigationManifest.Destinations)
        {
            Assert.Contains($"href=\"{destination.Route}\"", html);
            Assert.Contains($"data-ellai-control=\"{destination.ControlId}\"", html);
            Assert.Contains($"data-bonboarding-anchor=\"{destination.Anchor}\"", html);
        }
    }

    [Theory]
    [InlineData(Breakpoint.Xs)]
    [InlineData(Breakpoint.Md)]
    public async Task GuidanceRevealOpensExistingDrawerAndWorkspaceGroup(Breakpoint breakpoint)
    {
        await using var services = CreateServices(breakpoint, out _);
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        using var host = await renderer.RenderAsync<AuthorizedLayout>();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var reveal = (EventCallback)renderer.GetComponentParameter(host.ComponentId,
                typeof(EllaiGuidanceHost), nameof(EllaiGuidanceHost.RevealNavigation))!;
            await reveal.InvokeAsync();
            Assert.True(GetDrawerOpen(renderer, host.ComponentId, NavigationDrawerId));
            Assert.Equal(true, renderer.GetComponentParameter(host.ComponentId, typeof(MudNavGroup), nameof(MudNavGroup.Expanded)));
            Assert.Equal(true, renderer.GetComponentParameter(host.ComponentId, typeof(SwiftAidNavMenu), nameof(SwiftAidNavMenu.IsNavigationVisible)));
            var toggle = (EventCallback<MouseEventArgs>)renderer.GetComponentParameter(host.ComponentId,
                typeof(MudIconButton), nameof(MudIconButton.OnClick))!;
            await toggle.InvokeAsync(new MouseEventArgs());
            Assert.False(GetDrawerOpen(renderer, host.ComponentId, NavigationDrawerId));
            await reveal.InvokeAsync();
            Assert.True(GetDrawerOpen(renderer, host.ComponentId, NavigationDrawerId));
            Assert.Equal(1, renderer.GetComponentCount(host.ComponentId, typeof(MudDrawer)));
        });
    }

    [Fact]
    public async Task AuthorizedLayout_RendersEllaiLauncherConnectedToGuidanceHost()
    {
        await using var serviceProvider = CreateServices(Breakpoint.Md, out _);
        var launcher = serviceProvider.GetRequiredService<EllaiLauncher>();
        EllaiMode? requestedMode = null;
        launcher.Requested += mode => requestedMode = mode;
        var renderer = new TestRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());
        try
        {
            var host = await renderer.RenderAsync<AuthorizedLayout>();
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                Assert.True(launcher.HasSubscribers);
                var selected = (EventCallback<EllaiMode>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(EllaiLauncherMenu),
                    nameof(EllaiLauncherMenu.OnModeSelected))!;

                await selected.InvokeAsync(EllaiMode.ReviewEvidence);
                Assert.True(launcher.HasSubscribers);
                Assert.Equal(EllaiMode.ReviewEvidence, requestedMode);
            });
            host.Dispose();
        }
        finally
        {
            await renderer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Toggle_ExposesDrawerRelationshipAndCurrentExpandedState()
    {
        await using var serviceProvider = CreateServices(Breakpoint.Xs, out _);
        var renderer = new TestRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());
        try
        {
            var host = await renderer.RenderAsync<AuthorizedLayout>();
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var initialDrawerOpen = (bool)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Open),
                    NavigationDrawerId)!;
                Assert.False(initialDrawerOpen);
                Assert.Equal(1, renderer.GetComponentCount(host.ComponentId, typeof(MudDrawer)));
                Assert.Equal(DrawerVariant.Responsive, renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Variant)));
                Assert.Equal(
                    initialDrawerOpen ? "true" : "false",
                    renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-expanded"));
                Assert.Equal("Open navigation",
                    renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-label"));
                Assert.Equal(NavigationDrawerId, renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-controls"));
                Assert.Equal(NavigationDrawerId, renderer.GetComponentParameter(host.ComponentId, typeof(MudDrawer), "id", NavigationDrawerId));
                Assert.Equal(NavigationDrawerId, renderer.GetRenderedAttribute(host.ComponentId, typeof(MudDrawer), "id", NavigationDrawerId));

                var callback = (EventCallback<MouseEventArgs>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudIconButton),
                    nameof(MudIconButton.OnClick))!;
                await callback.InvokeAsync(new MouseEventArgs());

                var drawerOpenAfterToggle = (bool)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Open),
                    NavigationDrawerId)!;
                Assert.NotEqual(initialDrawerOpen, drawerOpenAfterToggle);
                Assert.Equal(
                    drawerOpenAfterToggle ? "true" : "false",
                    renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-expanded"));
                Assert.Equal("Close navigation",
                    renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-label"));
                Assert.Equal(NavigationDrawerId, renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-controls"));
            });
            host.Dispose();
        }
        finally
        {
            await renderer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Layout_UsesOneResponsiveDrawerAtDesktopBreakpoint()
    {
        await using var serviceProvider = CreateServices(Breakpoint.Md, out var viewport);
        var renderer = new TestRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());
        try
        {
            var host = await renderer.RenderAsync<AuthorizedLayout>();
            await viewport.ChangeBreakpointAsync(Breakpoint.Sm);
            await viewport.ChangeBreakpointAsync(Breakpoint.Md);
            await renderer.Dispatcher.InvokeAsync(() =>
            {
                Assert.Equal(1, renderer.GetComponentCount(host.ComponentId, typeof(MudDrawer)));
                Assert.Equal(DrawerVariant.Responsive, renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Variant)));
                Assert.Equal(true, renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Open)));
                Assert.Equal(Breakpoint.Md, renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Breakpoint)));
            });
            host.Dispose();
        }
        finally
        {
            await renderer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Layout_KeepsOneResponsiveDrawerWhenViewportCrossesDesktopBreakpoint()
    {
        await using var serviceProvider = CreateServices(Breakpoint.Xs, out var viewport);
        var renderer = new TestRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());
        try
        {
            var host = await renderer.RenderAsync<AuthorizedLayout>();
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                Assert.Equal(1, renderer.GetComponentCount(host.ComponentId, typeof(MudDrawer)));
                Assert.Equal(DrawerVariant.Responsive, renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Variant)));

                await viewport.ChangeBreakpointAsync(Breakpoint.Md);

                Assert.Equal(1, renderer.GetComponentCount(host.ComponentId, typeof(MudDrawer)));
                Assert.Equal(DrawerVariant.Responsive, renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Variant)));
                Assert.Equal(true, renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Open)));

                await viewport.ChangeBreakpointAsync(Breakpoint.Sm);

                Assert.Equal(1, renderer.GetComponentCount(host.ComponentId, typeof(MudDrawer)));
                Assert.Equal(DrawerVariant.Responsive, renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Variant)));
                Assert.Equal(false, renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Open)));
            });
            host.Dispose();
        }
        finally
        {
            await renderer.DisposeAsync();
        }
    }

    [Fact]
    public async Task MobileDrawerToggleControlsTheSingleResponsiveDrawer()
    {
        await using var serviceProvider = CreateServices(Breakpoint.Xs, out _);
        var renderer = new TestRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());
        try
        {
            var host = await renderer.RenderAsync<AuthorizedLayout>();
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var toggle = (EventCallback<MouseEventArgs>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudIconButton),
                    nameof(MudIconButton.OnClick))!;
                Assert.Equal(1, renderer.GetComponentCount(host.ComponentId, typeof(MudDrawer)));
                await toggle.InvokeAsync(new MouseEventArgs());
                Assert.True(GetDrawerOpen(renderer, host.ComponentId, NavigationDrawerId));

                await toggle.InvokeAsync(new MouseEventArgs());
                Assert.False(GetDrawerOpen(renderer, host.ComponentId, NavigationDrawerId));
                Assert.Equal("false", renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-expanded"));
            });
            host.Dispose();
        }
        finally
        {
            await renderer.DisposeAsync();
        }
    }

    private static bool GetDrawerOpen(TestRenderer renderer, int componentId, string drawerId) =>
        (bool)renderer.GetComponentParameter(componentId, typeof(MudDrawer), nameof(MudDrawer.Open), drawerId)!;

    private static ServiceProvider CreateServices(Breakpoint initialBreakpoint, out FakeBrowserViewportService viewport)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        viewport = new FakeBrowserViewportService(initialBreakpoint);
        services.AddSingleton<IBrowserViewportService>(viewport);
        BitBonboarding.AddBitBonboardingServices(services, _ => { });
        BitBonboarding.AddBitBonboardingInMemoryStore(services);
        services.AddSingleton<IBonboardingService>(DispatchProxy.Create<IBonboardingService, NoOpBonboardingProxy>());
        services.AddSingleton<IEllaiGuidanceControl>(DispatchProxy.Create<IEllaiGuidanceControl, NoOpGuidanceProxy>());
        services.AddScoped<EllaiLauncher>();
        var reviewClient = DispatchProxy.Create<IEllaiReviewClient, NoOpReviewClientProxy>();
        services.AddSingleton(reviewClient);
        services.AddSingleton(new EllaiEventPoller(reviewClient));
        return services.BuildServiceProvider();
    }

#pragma warning disable BL0006 // Render-tree access is required to exercise the real component event callback.
    private sealed class TestRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public async Task<RenderedComponent<TComponent>> RenderAsync<TComponent>()
            where TComponent : IComponent
        {
            TComponent component = default!;
            var componentId = -1;
            await Dispatcher.InvokeAsync(async () =>
            {
                component = (TComponent)InstantiateComponent(typeof(TComponent));
                componentId = AssignRootComponentId(component);
                await RenderRootComponentAsync(componentId);
            });

            return new RenderedComponent<TComponent>(this, component, componentId);
        }

        public object? GetComponentParameter(int rootId, Type componentType, string name, string? componentId = null)
        {
            return FindComponentParameter(rootId, componentType, name, componentId);
        }

        public int GetComponentCount(int rootId, Type componentType) => FindComponentIds(rootId, componentType).Count();

        public string GetElementAttribute(int rootId, string elementId, string name)
        {
            var frames = GetCurrentRenderTreeFrames(rootId);
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != RenderTreeFrameType.Element) continue;
                string? id = null;
                string? value = null;
                for (var j = i + 1; j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
                {
                    var attribute = frames.Array[j];
                    if (attribute.AttributeName == "id") id = attribute.AttributeValue?.ToString();
                    if (attribute.AttributeName == name) value = attribute.AttributeValue?.ToString();
                }
                if (id == elementId) return value ?? string.Empty;
            }
            throw new InvalidOperationException($"Element {elementId} was not rendered.");
        }

        public string? GetRenderedAttribute(int rootId, Type componentType, string attributeName, string? componentId = null)
        {
            foreach (var currentComponentId in FindComponentIds(rootId, componentType))
            {
                var frames = GetCurrentRenderTreeFrames(currentComponentId);
                string? candidateId = null;
                string? candidateValue = null;
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames.Array[index];
                    if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "id")
                    {
                        candidateId = frame.AttributeValue?.ToString();
                    }

                    if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == attributeName)
                        candidateValue = frame.AttributeValue?.ToString();
                }

                if (candidateValue is not null && (componentId is null || candidateId == componentId))
                    return candidateValue;
            }

            return null;
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

                foreach (var childId in FindComponentIds(frame.ComponentId, componentType))
                {
                    yield return childId;
                }
            }
        }

        private object? FindComponentParameter(int parentId, Type componentType, string name, string? componentId)
        {
            var frames = GetCurrentRenderTreeFrames(parentId);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Component)
                {
                    continue;
                }

                if (componentType.IsAssignableFrom(frame.ComponentType))
                {
                    string? candidateId = null;
                    object? candidateValue = null;
                    for (var attributeIndex = index + 1; attributeIndex < frames.Count; attributeIndex++)
                    {
                        var attribute = frames.Array[attributeIndex];
                        if (attribute.FrameType != RenderTreeFrameType.Attribute)
                        {
                            break;
                        }

                        if (attribute.AttributeName == "id")
                        {
                            candidateId = attribute.AttributeValue?.ToString();
                        }

                        if (attribute.AttributeName == name)
                        {
                            candidateValue = attribute.AttributeValue;
                        }
                    }

                    if (candidateValue is not null && (componentId is null || candidateId == componentId))
                    {
                        return candidateValue;
                    }
                }

                var nestedValue = FindComponentParameter(frame.ComponentId, componentType, name, componentId);
                if (nestedValue is not null)
                {
                    return nestedValue;
                }
            }

            return null;
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        protected override void HandleException(Exception exception) => throw exception;

        public void Unmount(int componentId) => RemoveRootComponent(componentId);

    }
#pragma warning restore BL0006

    private sealed class RenderedComponent<TComponent>
        : IDisposable
        where TComponent : IComponent
    {
        private readonly TestRenderer _renderer;
        private readonly int _componentId;

        public RenderedComponent(TestRenderer renderer, TComponent component, int componentId)
        {
            _renderer = renderer;
            Component = component;
            _componentId = componentId;
            ComponentId = componentId;
        }

        public TComponent Component { get; }
        public int ComponentId { get; }

        public void Dispose() => _renderer.Dispatcher.InvokeAsync(() => _renderer.Unmount(_componentId))
            .GetAwaiter().GetResult();
    }

    private sealed class FakeBrowserViewportService(Breakpoint initialBreakpoint) : IBrowserViewportService
    {
        private readonly Dictionary<Guid, Func<BrowserViewportEventArgs, Task>> _observers = [];
        private readonly Dictionary<Guid, IBrowserViewportObserver> _interfaceObservers = [];
        private Breakpoint _currentBreakpoint = initialBreakpoint;

        public ResizeOptions ResizeOptions { get; } = new();

        public Task SubscribeAsync(IBrowserViewportObserver observer, bool fireImmediately = true)
        {
            _interfaceObservers[observer.Id] = observer;
            return fireImmediately ? observer.NotifyBrowserViewportChangeAsync(CreateEventArgs(isImmediate: true)) : Task.CompletedTask;
        }

        public Task SubscribeAsync(Guid observerId, Action<BrowserViewportEventArgs> callback, ResizeOptions? options = null, bool fireImmediately = true)
        {
            _observers[observerId] = args =>
            {
                callback(args);
                return Task.CompletedTask;
            };
            return fireImmediately ? _observers[observerId](CreateEventArgs(isImmediate: true)) : Task.CompletedTask;
        }

        public Task SubscribeAsync(Guid observerId, Func<BrowserViewportEventArgs, Task> callback, ResizeOptions? options = null, bool fireImmediately = true)
        {
            _observers[observerId] = callback;
            return fireImmediately ? callback(CreateEventArgs(isImmediate: true)) : Task.CompletedTask;
        }

        public Task UnsubscribeAsync(IBrowserViewportObserver observer)
        {
            _interfaceObservers.Remove(observer.Id);
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(Guid observerId)
        {
            _observers.Remove(observerId);
            return Task.CompletedTask;
        }

        public Task<bool> IsMediaQueryMatchAsync(string mediaQuery) => Task.FromResult(false);

        public Task<bool> IsBreakpointWithinWindowSizeAsync(Breakpoint breakpoint) =>
            Task.FromResult(_currentBreakpoint == breakpoint);

        public Task<bool> IsBreakpointWithinReferenceSizeAsync(Breakpoint breakpoint, Breakpoint reference) =>
            Task.FromResult(breakpoint switch
            {
                Breakpoint.None => false,
                Breakpoint.Always => true,
                Breakpoint.Xs => reference == Breakpoint.Xs,
                Breakpoint.Sm => reference == Breakpoint.Sm,
                Breakpoint.Md => reference == Breakpoint.Md,
                Breakpoint.Lg => reference == Breakpoint.Lg,
                Breakpoint.Xl => reference == Breakpoint.Xl,
                Breakpoint.Xxl => reference == Breakpoint.Xxl,
                Breakpoint.SmAndDown => reference <= Breakpoint.Sm,
                Breakpoint.MdAndDown => reference <= Breakpoint.Md,
                Breakpoint.LgAndDown => reference <= Breakpoint.Lg,
                Breakpoint.XlAndDown => reference <= Breakpoint.Xl,
                Breakpoint.SmAndUp => reference >= Breakpoint.Sm,
                Breakpoint.MdAndUp => reference >= Breakpoint.Md,
                Breakpoint.LgAndUp => reference >= Breakpoint.Lg,
                Breakpoint.XlAndUp => reference >= Breakpoint.Xl,
                _ => false
            });

        public Task<Breakpoint> GetCurrentBreakpointAsync() => Task.FromResult(_currentBreakpoint);

        public Task<BrowserWindowSize> GetCurrentBrowserWindowSizeAsync() =>
            Task.FromResult(new BrowserWindowSize { Width = 1280, Height = 800 });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task ChangeBreakpointAsync(Breakpoint breakpoint)
        {
            _currentBreakpoint = breakpoint;
            var args = CreateEventArgs(isImmediate: false);
            foreach (var observer in _interfaceObservers.Values.ToArray())
            {
                await observer.NotifyBrowserViewportChangeAsync(args);
            }

            foreach (var observer in _observers.Values.ToArray())
            {
                await observer(args);
            }
        }

        private BrowserViewportEventArgs CreateEventArgs(bool isImmediate) => new(
            Guid.NewGuid(),
            new BrowserWindowSize { Width = _currentBreakpoint is Breakpoint.Xs or Breakpoint.Sm ? 390 : 1280, Height = 800 },
            _currentBreakpoint,
            isImmediate);
    }

    public class NoOpBonboardingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(void)
                ? null
                : targetMethod?.ReturnType == typeof(Task)
                    ? Task.CompletedTask
                    : targetMethod?.ReturnType.IsValueType == true
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null;
    }

    public class NoOpGuidanceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(Task)
                ? Task.CompletedTask
                : targetMethod?.ReturnType.IsValueType == true
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
    }

    public class NoOpReviewClientProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType;
            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType?.IsGenericType == true && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GenericTypeArguments[0];
                var value = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, [value]);
            }

            return returnType?.IsValueType == true ? Activator.CreateInstance(returnType) : null;
        }
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(typeof(TValue) == typeof(IJSObjectReference)
                ? (TValue)(object)new NoOpJsObjectReference()
                : default!);
    }

    private sealed class NoOpJsObjectReference : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }
}
#pragma warning restore BL0006
