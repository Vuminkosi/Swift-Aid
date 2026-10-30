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
using SwiftAid.UI.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

#pragma warning disable BL0006 // Render-tree access is required to exercise the real component event callback.
public sealed class AuthorizedLayoutNavigationAccessibilityTests
{
    private const string DrawerId = "swiftaid-authorized-navigation";

    [Fact]
    public async Task Toggle_ExposesDrawerRelationshipAndCurrentExpandedState()
    {
        await using var serviceProvider = CreateServices(Breakpoint.Xs, out var viewport);
        var renderer = new TestRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());
        try
        {
            var host = await renderer.RenderAsync<AuthorizedLayout>();
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var initialDrawerOpen = (bool)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Open))!;
                Assert.False(initialDrawerOpen);
                Assert.Equal(
                    initialDrawerOpen ? "true" : "false",
                    renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-expanded"));
                Assert.Equal(DrawerId, renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-controls"));
                Assert.Equal(DrawerId, renderer.GetComponentParameter(host.ComponentId, typeof(MudDrawer), "id"));
                Assert.Equal(DrawerId, renderer.GetRenderedAttribute(host.ComponentId, typeof(MudDrawer), "id"));

                var callback = (EventCallback<MouseEventArgs>)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudIconButton),
                    nameof(MudIconButton.OnClick))!;
                await callback.InvokeAsync(new MouseEventArgs());

                var drawerOpenAfterToggle = (bool)renderer.GetComponentParameter(
                    host.ComponentId,
                    typeof(MudDrawer),
                    nameof(MudDrawer.Open))!;
                Assert.NotEqual(initialDrawerOpen, drawerOpenAfterToggle);
                Assert.Equal(
                    drawerOpenAfterToggle ? "true" : "false",
                    renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-expanded"));
                Assert.Equal(DrawerId, renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-controls"));
            });
            host.Dispose();
        }
        finally
        {
            await renderer.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(Breakpoint.Xs, false)]
    [InlineData(Breakpoint.Sm, false)]
    [InlineData(Breakpoint.Md, true)]
    [InlineData(Breakpoint.Lg, true)]
    public async Task InitialViewport_SetsResponsiveDrawerDefault(Breakpoint breakpoint, bool expectedOpen)
    {
        await using var serviceProvider = CreateServices(breakpoint, out _);
        var renderer = new TestRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());
        try
        {
            var host = await renderer.RenderAsync<AuthorizedLayout>();
            await renderer.Dispatcher.InvokeAsync(() =>
            {
                Assert.Equal(expectedOpen, renderer.GetComponentParameter(
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
    public async Task BreakpointCrossingsChangeDrawerDefaultButResizesWithinRangePreserveChoice()
    {
        await using var serviceProvider = CreateServices(Breakpoint.Xs, out var viewport);
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
                await toggle.InvokeAsync(new MouseEventArgs());
                Assert.True(GetDrawerOpen(renderer, host.ComponentId));

                await viewport.ChangeBreakpointAsync(Breakpoint.Sm);
                Assert.True(GetDrawerOpen(renderer, host.ComponentId));

                await viewport.ChangeBreakpointAsync(Breakpoint.Md);
                Assert.True(GetDrawerOpen(renderer, host.ComponentId));
                Assert.Equal("true", renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-expanded"));

                await toggle.InvokeAsync(new MouseEventArgs());
                Assert.False(GetDrawerOpen(renderer, host.ComponentId));
                await viewport.ChangeBreakpointAsync(Breakpoint.Lg);
                Assert.False(GetDrawerOpen(renderer, host.ComponentId));

                await viewport.ChangeBreakpointAsync(Breakpoint.Sm);
                Assert.False(GetDrawerOpen(renderer, host.ComponentId));
                Assert.Equal("false", renderer.GetRenderedAttribute(host.ComponentId, typeof(MudIconButton), "aria-expanded"));
            });
            host.Dispose();
        }
        finally
        {
            await renderer.DisposeAsync();
        }
    }

    private static bool GetDrawerOpen(TestRenderer renderer, int componentId) =>
        (bool)renderer.GetComponentParameter(componentId, typeof(MudDrawer), nameof(MudDrawer.Open))!;

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

        public object? GetComponentParameter(int rootId, Type componentType, string name)
        {
            return FindComponentParameter(rootId, componentType, name);
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

        private object? FindComponentParameter(int parentId, Type componentType, string name)
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

                var nestedValue = FindComponentParameter(frame.ComponentId, componentType, name);
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
            Task.FromResult(_currentBreakpoint == breakpoint);

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
