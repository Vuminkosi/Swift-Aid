using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using SwiftAid.Contracts.Cases;
using SwiftAid.UI.Components.Cases;

namespace SwiftAid.Ui.Tests;

#pragma warning disable BL0006
public sealed class CaseTimelinePaginationUiTests
{
    [Fact]
    public async Task LoadOlderHistory_UsesCursorAndMergesPagesInRecordedOrder()
    {
        var caseId = Guid.NewGuid();
        var client = new PagingTimelineClient(caseId);
        var services = CreateServices();
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        using var rendered = await renderer.RenderAsync(client, caseId);

        Assert.Equal([3L, 4L], GetTimeline(rendered.Instance).Entries.Select(entry => entry.AggregateVersion));
        await renderer.InvokeFirstButtonAsync(rendered.ComponentId);

        Assert.Equal(3, client.LastCursor);
        Assert.Equal([1L, 2L, 3L, 4L], GetTimeline(rendered.Instance).Entries.Select(entry => entry.AggregateVersion));
        Assert.False(GetTimeline(rendered.Instance).HasOlderEntries);
    }

    [Fact]
    public async Task InitialPage_RejectsCursorThatCouldSkipHistory()
    {
        var caseId = Guid.NewGuid();
        var services = CreateServices();
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        using var rendered = await renderer.RenderAsync(new PagingTimelineClient(caseId, wrongCursor: true), caseId);

        Assert.Null(typeof(CaseTimelineView)
            .GetField("_timeline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(rendered.Instance));
        Assert.NotNull(typeof(CaseTimelineView)
            .GetField("_error", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(rendered.Instance));
    }

    private static CaseTimeline GetTimeline(object component) =>
        Assert.IsType<CaseTimeline>(typeof(CaseTimelineView)
            .GetField("_timeline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(component));

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, RecordingJsRuntime>();
        return services.BuildServiceProvider();
    }

    private sealed class PagingTimelineClient(Guid caseId, bool wrongCursor = false) : ICaseTimelineClient
    {
        private readonly CaseTimelineHeader _header = new(
            caseId, "org-1", "workspace-1", "SZ", "Active", "actor-1", 4, 1, DateTimeOffset.UnixEpoch);

        public long? LastCursor { get; private set; }

        public Task<CaseTimelineResult> GetAsync(
            Guid requestedCaseId,
            int? limit,
            CancellationToken cancellationToken = default,
            long? beforeAggregateVersion = null)
        {
            LastCursor = beforeAggregateVersion;
            var entries = beforeAggregateVersion is null
                ? new[] { Entry(3), Entry(4) }
                : new[] { Entry(1), Entry(2) };
            var timeline = beforeAggregateVersion is null
                ? new CaseTimeline(_header, entries, HasOlderEntries: true, OlderThanAggregateVersion: wrongCursor ? 2 : 3)
                : new CaseTimeline(_header, entries);
            return Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.Found, timeline));
        }

        private static CaseTimelineEntry Entry(long version) => new(
            Guid.NewGuid(), $"event-{version}", $"Event {version}", CaseTimelineEntryKind.Authoritative,
            version, 1, Guid.NewGuid(), "actor-1", null, DateTimeOffset.UnixEpoch.AddMinutes(version), 1);
    }

    private sealed class TestRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public async Task<RenderedComponent> RenderAsync(ICaseTimelineClient client, Guid caseId)
        {
            var component = new CaseTimelineView();
            var id = -1;
            await Dispatcher.InvokeAsync(async () =>
            {
                id = AssignRootComponentId(component);
                await RenderRootComponentAsync(id, ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CaseTimelineView.Client)] = client,
                    [nameof(CaseTimelineView.CaseId)] = caseId
                }));
            });
            return new RenderedComponent(this, id, component);
        }

        public async Task InvokeFirstButtonAsync(int rootId)
        {
            var frames = GetCurrentRenderTreeFrames(rootId);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Component || frame.ComponentType != typeof(MudButton))
                {
                    continue;
                }

                var callback = GetButtonCallback(frames, index);
                await Dispatcher.InvokeAsync(() => callback.InvokeAsync(new MouseEventArgs()));
                return;
            }

            throw new InvalidOperationException("No history action button was rendered.");
        }

        private static EventCallback<MouseEventArgs> GetButtonCallback(
            ArrayRange<RenderTreeFrame> frames,
            int componentIndex)
        {
            for (var index = componentIndex + 1; index < frames.Count && frames.Array[index].FrameType == RenderTreeFrameType.Attribute; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == nameof(MudButton.OnClick))
                {
                    return Assert.IsType<EventCallback<MouseEventArgs>>(frame.AttributeValue);
                }
            }

            throw new InvalidOperationException("The history action button has no click callback.");
        }

        public void Unmount(int componentId) => Dispatcher.InvokeAsync(() => RemoveRootComponent(componentId)).GetAwaiter().GetResult();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }

    private sealed class RenderedComponent(TestRenderer renderer, int componentId, object instance) : IDisposable
    {
        public int ComponentId => componentId;
        public object Instance => instance;
        public void Dispose() => renderer.Unmount(componentId);
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
