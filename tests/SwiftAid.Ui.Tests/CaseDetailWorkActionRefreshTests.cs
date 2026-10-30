using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Components.Pages;
using SwiftAid.UI.Components.Surfaces.Operations;
using SwiftAid.UI.Components.Surfaces.Operations.CaseDetail;
using SwiftAid.UI.Components.Surfaces.Operations.WorkManagement;

namespace SwiftAid.Ui.Tests;

#pragma warning disable BL0006 // The test renderer inspects component parameters to exercise the real page callback.
public sealed class CaseDetailWorkActionRefreshTests
{
    [Fact]
    public async Task AcceptedAction_WhenRefreshFails_DoesNotClaimWorkWasRefreshed()
    {
        var caseId = Guid.NewGuid();
        var client = new FakeWorkManagementClient(failRefresh: true);
        var (services, renderer) = CreateRenderer(client);
        await using (services)
        await using (renderer)
        {
            var page = await renderer.RenderAsync<CaseDetail>(caseId);
            try
            {
                await InvokeActionAsync(renderer, page.ComponentId, caseId);

                var message = GetSurfaceParameter<string>(
                    renderer,
                    page.ComponentId,
                    nameof(OperationsCaseDetailSurface.WorkStatusMessage));
                var isError = GetSurfaceParameter<bool>(
                    renderer,
                    page.ComponentId,
                    nameof(OperationsCaseDetailSurface.WorkStatusIsError));
                var state = GetSurfaceParameter<OperationsSurfaceState>(
                    renderer,
                    page.ComponentId,
                    nameof(OperationsCaseDetailSurface.WorkState));
                var retry = GetSurfaceParameter<EventCallback>(
                    renderer,
                    page.ComponentId,
                    nameof(OperationsCaseDetailSurface.OnWorkRetry));

                Assert.Equal(2, client.GetCalls);
                Assert.Equal(1, client.ExecuteCalls);
                Assert.False(string.IsNullOrWhiteSpace(message));
                Assert.DoesNotContain(
                    "coordination work is refreshed.",
                    message,
                    StringComparison.OrdinalIgnoreCase);
                Assert.True(isError);
                Assert.Equal(OperationsSurfaceState.Unavailable, state);
                Assert.True(retry.HasDelegate);
            }
            finally
            {
                page.Dispose();
            }
        }
    }

    [Fact]
    public async Task AcceptedAction_WhenRefreshSucceeds_ShowsRefreshedSuccess()
    {
        var caseId = Guid.NewGuid();
        var client = new FakeWorkManagementClient(failRefresh: false);
        var (services, renderer) = CreateRenderer(client);
        await using (services)
        await using (renderer)
        {
            var page = await renderer.RenderAsync<CaseDetail>(caseId);
            try
            {
                await InvokeActionAsync(renderer, page.ComponentId, caseId);

                var message = GetSurfaceParameter<string>(
                    renderer,
                    page.ComponentId,
                    nameof(OperationsCaseDetailSurface.WorkStatusMessage));
                var isError = GetSurfaceParameter<bool>(
                    renderer,
                    page.ComponentId,
                    nameof(OperationsCaseDetailSurface.WorkStatusIsError));

                Assert.Equal(2, client.GetCalls);
                Assert.Equal(1, client.ExecuteCalls);
                Assert.Equal("Action completed. Coordination work is refreshed.", message);
                Assert.False(isError);
            }
            finally
            {
                page.Dispose();
            }
        }
    }

    [Fact]
    public async Task UnavailableMutation_DoesNotReportActionAccepted()
    {
        var caseId = Guid.NewGuid();
        var client = new FakeWorkManagementClient(failRefresh: false, failMutation: true);
        var (services, renderer) = CreateRenderer(client);
        await using (services)
        await using (renderer)
        {
            var page = await renderer.RenderAsync<CaseDetail>(caseId);
            try
            {
                await InvokeActionAsync(renderer, page.ComponentId, caseId);

                var message = GetSurfaceParameter<string>(
                    renderer,
                    page.ComponentId,
                    nameof(OperationsCaseDetailSurface.WorkStatusMessage));
                var state = GetSurfaceParameter<OperationsSurfaceState>(
                    renderer,
                    page.ComponentId,
                    nameof(OperationsCaseDetailSurface.WorkState));

                Assert.Equal(1, client.GetCalls);
                Assert.Equal(1, client.ExecuteCalls);
                Assert.DoesNotContain("Action accepted", message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(OperationsSurfaceState.Unavailable, state);
            }
            finally
            {
                page.Dispose();
            }
        }
    }

    private static async Task InvokeActionAsync(TestRenderer renderer, int rootId, Guid caseId)
    {
        var action = (EventCallback<OperationsWorkManagementSurface.OperationsWorkActionRequest>?)
            renderer.GetComponentParameter(
                rootId,
                typeof(OperationsCaseDetailSurface),
                nameof(OperationsCaseDetailSurface.OnWorkAction));

        Assert.True(action.HasValue);
        await renderer.Dispatcher.InvokeAsync(() => action!.Value.InvokeAsync(new(
            caseId,
            "synthetic-follow-up",
            1,
            OperationsWorkManagementSurface.OperationsWorkAction.RecordFollowUpOutcome,
            null,
            null,
            "Synthetic outcome",
            OperationsWorkManagementSurface.FollowUpOutcome.ContactMade,
            null,
            null,
            false)));
    }

    private static T GetSurfaceParameter<T>(TestRenderer renderer, int rootId, string name)
    {
        var value = renderer.GetComponentParameter(rootId, typeof(OperationsCaseDetailSurface), name);
        Assert.IsType<T>(value);
        return (T)value!;
    }

    private static (ServiceProvider Services, TestRenderer Renderer) CreateRenderer(FakeWorkManagementClient workClient)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoOpJsRuntime>();
        services.AddSingleton<ICaseTimelineClient, EmptyCaseTimelineClient>();
        services.AddSingleton<ICaseDiscussionClient, EmptyCaseDiscussionClient>();
        services.AddSingleton<ICaseEvidenceClient, EmptyCaseEvidenceClient>();
        services.AddSingleton<IWorkManagementClient>(workClient);

        var serviceProvider = services.BuildServiceProvider();
        return (serviceProvider, new TestRenderer(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>()));
    }

    private sealed class FakeWorkManagementClient(bool failRefresh, bool failMutation = false)
        : IWorkManagementClient
    {
        private int _getCalls;

        public int GetCalls => _getCalls;
        public int ExecuteCalls { get; private set; }

        public Task<UiCapabilityResult<WorkManagementSnapshot>> GetAsync(
            Guid requestedCaseId,
            WorkManagementQuery query,
            CancellationToken cancellationToken = default)
        {
            _getCalls++;
            if (_getCalls > 1 && failRefresh)
            {
                return Task.FromResult(UiCapabilityResults.Unavailable<WorkManagementSnapshot>("synthetic-work"));
            }

            var version = _getCalls > 1 ? 2 : 1;
            return Task.FromResult(Success(CreateSnapshot(requestedCaseId, version)));
        }

        public Task<UiCapabilityResult<WorkManagementSnapshot>> ExecuteAsync(
            WorkManagementCommand command,
            WorkManagementQuery query,
            CancellationToken cancellationToken = default)
        {
            ExecuteCalls++;
            if (failMutation)
            {
                return Task.FromResult(UiCapabilityResults.Unavailable<WorkManagementSnapshot>("synthetic-work"));
            }

            return Task.FromResult(Success(CreateSnapshot(command.CaseId, command.ExpectedVersion + 1)));
        }

        private static WorkManagementSnapshot CreateSnapshot(Guid caseId, long version)
        {
            var item = new WorkManagementItem(
                "synthetic-follow-up",
                "Synthetic follow-up",
                WorkManagementKind.FollowUp,
                WorkManagementStatus.Scheduled,
                WorkManagementAssignmentStatus.Unassigned,
                "Synthetic follow-up item for UI regression coverage.",
                DateTimeOffset.UtcNow,
                null,
                [WorkManagementAction.RecordFollowUpOutcome],
                [],
                []);
            return new WorkManagementSnapshot(
                caseId,
                version,
                new Page<WorkManagementItem>([item], 1, 100, 1, false));
        }

        private static UiCapabilityResult<WorkManagementSnapshot> Success(WorkManagementSnapshot snapshot) => new(
            UiCapabilityResultCode.Success,
            snapshot,
            new UiCapabilityMetadata(
                "synthetic-work",
                "1",
                UiCapabilityProvenance.SyntheticDevelopment,
                DateTimeOffset.UtcNow,
                IsAvailable: true));
    }

    private sealed class EmptyCaseTimelineClient : ICaseTimelineClient
    {
        public Task<CaseTimelineResult> GetAsync(Guid caseId, int? limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null));
    }

    private sealed class EmptyCaseDiscussionClient : ICaseDiscussionClient
    {
        public Task<UiCapabilityResult<CaseDiscussionSnapshot>> GetAsync(
            Guid caseId,
            CaseDiscussionQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(UiCapabilityResults.Unavailable<CaseDiscussionSnapshot>("synthetic-discussion"));
    }

    private sealed class EmptyCaseEvidenceClient : ICaseEvidenceClient
    {
        public Task<UiCapabilityResult<CaseEvidenceSnapshot>> GetAsync(
            Guid caseId,
            CaseEvidenceQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(UiCapabilityResults.Unavailable<CaseEvidenceSnapshot>("synthetic-evidence"));

        public Task<UiCapabilityResult<CaseEvidenceDetail>> GetDetailAsync(
            Guid caseId,
            string evidenceKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(UiCapabilityResults.Unavailable<CaseEvidenceDetail>("synthetic-evidence"));

        public Task<UiCapabilityResult<CaseEvidenceDownload>> DownloadAsync(
            Guid caseId,
            string evidenceKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(UiCapabilityResults.Unavailable<CaseEvidenceDownload>("synthetic-evidence"));
    }

    private sealed class TestRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public async Task<RenderedComponent<TComponent>> RenderAsync<TComponent>(Guid caseId)
            where TComponent : IComponent
        {
            TComponent component = default!;
            var componentId = -1;
            await Dispatcher.InvokeAsync(async () =>
            {
                component = (TComponent)InstantiateComponent(typeof(TComponent));
                componentId = AssignRootComponentId(component);
                await RenderRootComponentAsync(
                    componentId,
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(CaseDetail.CaseId)] = caseId
                    }));
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

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        protected override void HandleException(Exception exception) => throw exception;

        public void Unmount(int componentId) => RemoveRootComponent(componentId);
    }

    private sealed class RenderedComponent<TComponent>(TestRenderer renderer, int componentId) : IDisposable
        where TComponent : IComponent
    {
        public int ComponentId { get; } = componentId;

        public void Dispose() => renderer.Dispatcher.InvokeAsync(() => renderer.Unmount(ComponentId))
            .GetAwaiter()
            .GetResult();
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
#pragma warning restore BL0006
