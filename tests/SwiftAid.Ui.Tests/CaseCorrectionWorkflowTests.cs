using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using SwiftAid.Contracts.Cases;
using SwiftAid.UI.Components.Surfaces.Operations.CaseDetail;

namespace SwiftAid.Ui.Tests;

#pragma warning disable BL0006
public sealed class CaseCorrectionWorkflowTests
{
    [Theory]
    [InlineData(CaseDetailPanelState.Denied, CaseDetailCorrectionKind.LegacyV1, false, 0)]
    [InlineData(CaseDetailPanelState.Populated, CaseDetailCorrectionKind.HumanV2, false, 0)]
    [InlineData(CaseDetailPanelState.Populated, CaseDetailCorrectionKind.LegacyV1, false, 0)]
    [InlineData(CaseDetailPanelState.Populated, CaseDetailCorrectionKind.LegacyV1, true, 1)]
    public async Task CorrectAction_IsOnlyRenderedForAuthorizedLegacyProjection(
        CaseDetailPanelState state, CaseDetailCorrectionKind kind, bool canCorrect, int expectedCorrectionButtons)
    {
        var services = CreateServices();
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        using var rendered = await renderer.RenderAsync(state, kind, canCorrect);
        var buttons = renderer.FindComponents(rendered.ComponentId, typeof(MudButton));
        var correctionActions = buttons.Count(button => Equals(button.Parameters.GetValueOrDefault(nameof(MudButton.Class)), "case-detail__correction-action"));
        Assert.Equal(expectedCorrectionButtons, correctionActions);
    }

    [Fact]
    public async Task Conflict_RetainsBothDraftFields_AndDoesNotRefresh()
    {
        var caseId = Guid.NewGuid();
        var services = CreateServices();
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var component = await renderer.RenderAsync(CaseDetailPanelState.Populated, CaseDetailCorrectionKind.LegacyV1, true, caseId);
        using (component)
        {
            var surfaceId = component.ComponentId;
            await renderer.InvokeButtonAsync(surfaceId, Variant.Text, "case-detail__correction-action");
            var fields = renderer.FindComponents(surfaceId, typeof(MudTextField<string>));
            var correctionFields = fields.Where(field => field.Parameters.ContainsKey(nameof(MudTextField<string>.Label))).ToArray();
            Assert.Equal(2, correctionFields.Length);
            Assert.Contains(correctionFields, field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Corrected report"));
            Assert.Contains(correctionFields, field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Reason for correction"));
            await renderer.SetFieldValueAsync(correctionFields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Corrected report")), "revised narrative");
            await renderer.SetFieldValueAsync(correctionFields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Reason for correction")), "source confirmed");
            await renderer.InvokeButtonAsync(surfaceId, Variant.Filled, "case-detail__touch-action");

            var retained = renderer.FindComponents(surfaceId, typeof(MudTextField<string>))
                .Where(field => field.Parameters.ContainsKey(nameof(MudTextField<string>.Label))).ToArray();
            Assert.Equal("revised narrative", retained.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Corrected report")).Parameters[nameof(MudTextField<string>.Value)]);
            Assert.Equal("source confirmed", retained.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Reason for correction")).Parameters[nameof(MudTextField<string>.Value)]);
            Assert.NotEmpty(renderer.FindComponents(surfaceId, typeof(MudAlert)));
        }
    }

    [Fact]
    public async Task Conflict_RebaseKeepsDraftAndUsesLatestVersionWithNewCommandId()
    {
        var services = CreateServices();
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var original = new CaseDetailCorrectionView(CaseDetailPanelState.Populated, 8, 2, CaseDetailCorrectionKind.LegacyV1, true, "original narrative");
        var latest = new CaseDetailCorrectionView(CaseDetailPanelState.Populated, 9, 3, CaseDetailCorrectionKind.LegacyV1, true, "newer server narrative");
        CaseDetailCorrectionRequest? first = null;
        CaseDetailCorrectionRequest? second = null;
        var submits = 0;
        var parameters = CreateParameters(Guid.NewGuid(), original,
            () => Task.FromResult<CaseDetailCorrectionView?>(latest),
            request =>
            {
                submits++;
                if (submits == 1) first = request; else second = request;
                return Task.FromResult(new CaseDetailCorrectionOutcome(CaseDetailOperationStatus.Conflict));
            });
        using var component = await renderer.RenderAsync(parameters);
        var surfaceId = component.ComponentId;
        await renderer.InvokeButtonAsync(surfaceId, Variant.Text, "case-detail__correction-action");
        var fields = renderer.FindComponents(surfaceId, typeof(MudTextField<string>)).Where(field => field.Parameters.ContainsKey(nameof(MudTextField<string>.Label))).ToArray();
        await renderer.SetFieldValueAsync(fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Corrected report")), "my retained draft");
        await renderer.SetFieldValueAsync(fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Reason for correction")), "my retained reason");
        await renderer.InvokeButtonAsync(surfaceId, Variant.Filled, "case-detail__touch-action");
        await renderer.InvokeButtonAsync(surfaceId, Variant.Outlined, "case-detail__touch-action");
        var latestField = renderer.FindComponents(surfaceId, typeof(MudTextField<string>))
            .Single(field => Equals(field.Parameters.GetValueOrDefault(nameof(MudTextField<string>.Label)), "Latest authorized report"));
        Assert.Equal("newer server narrative", latestField.Parameters[nameof(MudTextField<string>.Value)]);
        var review = renderer.FindComponents(surfaceId, typeof(MudCheckBox<bool>)).Single();
        Assert.Contains(renderer.FindElements(surfaceId), element =>
            Equals(element.GetValueOrDefault("aria-label"), "Review the latest report before submitting") &&
            Equals(element.GetValueOrDefault("tabindex"), "-1"));
        await renderer.SetBooleanValueAsync(review, true);
        await renderer.InvokeButtonAsync(surfaceId, Variant.Filled, "case-detail__touch-action");

        Assert.Equal(8, Assert.IsType<CaseDetailCorrectionRequest>(first).ExpectedVersion);
        var rebased = Assert.IsType<CaseDetailCorrectionRequest>(second);
        Assert.Equal(9, rebased.ExpectedVersion);
        Assert.NotEqual(first!.CommandId, rebased.CommandId);
        Assert.Equal("my retained draft", rebased.Narrative);
        Assert.Equal("my retained reason", rebased.Reason);
    }

    [Fact]
    public async Task OpeningShowsFormAndCancelRestoresCorrectionTrigger()
    {
        var services = CreateServices();
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        using var component = await renderer.RenderAsync(CaseDetailPanelState.Populated, CaseDetailCorrectionKind.LegacyV1, true);
        var rootId = component.ComponentId;

        await renderer.InvokeButtonAsync(rootId, Variant.Text, "case-detail__correction-action");
        Assert.Equal(2, renderer.FindComponents(rootId, typeof(MudTextField<string>))
            .Count(field => Equals(field.Parameters.GetValueOrDefault(nameof(MudTextField<string>.Label)), "Corrected report") ||
                Equals(field.Parameters.GetValueOrDefault(nameof(MudTextField<string>.Label)), "Reason for correction")));
        Assert.DoesNotContain(renderer.FindComponents(rootId, typeof(MudButton)), button =>
            Equals(button.Parameters.GetValueOrDefault(nameof(MudButton.Class)), "case-detail__correction-action"));

        await renderer.InvokeButtonAsync(rootId, Variant.Text, "case-detail__touch-action");
        Assert.Contains(renderer.FindComponents(rootId, typeof(MudButton)), button =>
            Equals(button.Parameters.GetValueOrDefault(nameof(MudButton.Class)), "case-detail__correction-action"));
        Assert.DoesNotContain(renderer.FindComponents(rootId, typeof(MudTextField<string>)), field =>
            Equals(field.Parameters.GetValueOrDefault(nameof(MudTextField<string>.Label)), "Corrected report"));
    }

    [Fact]
    public async Task LostCorrectionCapabilityClearsProtectedDraftAndRetryIdentity()
    {
        var services = CreateServices();
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var caseId = Guid.NewGuid();
        var authorized = new CaseDetailCorrectionView(CaseDetailPanelState.Populated, 8, 2, CaseDetailCorrectionKind.LegacyV1, true, "original narrative");
        CaseDetailCorrectionRequest? first = null;
        CaseDetailCorrectionRequest? second = null;
        var submits = 0;
        var parameters = CreateParameters(caseId, authorized,
            () => Task.FromResult<CaseDetailCorrectionView?>(authorized),
            request =>
            {
                submits++;
                if (submits == 1) first = request; else second = request;
                return Task.FromResult(new CaseDetailCorrectionOutcome(CaseDetailOperationStatus.Conflict));
            });
        using var component = await renderer.RenderAsync(parameters);
        var rootId = component.ComponentId;
        await renderer.InvokeButtonAsync(rootId, Variant.Text, "case-detail__correction-action");
        var fields = renderer.FindComponents(rootId, typeof(MudTextField<string>)).Where(field => field.Parameters.ContainsKey(nameof(MudTextField<string>.Label))).ToArray();
        await renderer.SetFieldValueAsync(fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Corrected report")), "sensitive draft");
        await renderer.SetFieldValueAsync(fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Reason for correction")), "sensitive reason");
        await renderer.InvokeButtonAsync(rootId, Variant.Filled, "case-detail__touch-action");

        parameters[nameof(OperationsCaseDetailSurface.CorrectionData)] = new CaseDetailCorrectionView(CaseDetailPanelState.Offline);
        await renderer.UpdateAsync(rootId, parameters);
        Assert.DoesNotContain(renderer.FindComponents(rootId, typeof(MudTextField<string>)), field =>
            Equals(field.Parameters.GetValueOrDefault(nameof(MudTextField<string>.Label)), "Corrected report") ||
            Equals(field.Parameters.GetValueOrDefault(nameof(MudTextField<string>.Label)), "Reason for correction"));

        parameters[nameof(OperationsCaseDetailSurface.CorrectionData)] = new CaseDetailCorrectionView(CaseDetailPanelState.Denied);
        await renderer.UpdateAsync(rootId, parameters);
        Assert.DoesNotContain(renderer.FindComponents(rootId, typeof(MudTextField<string>)), field =>
            Equals(field.Parameters.GetValueOrDefault(nameof(MudTextField<string>.Label)), "Corrected report") ||
            Equals(field.Parameters.GetValueOrDefault(nameof(MudTextField<string>.Label)), "Reason for correction"));

        parameters[nameof(OperationsCaseDetailSurface.CorrectionData)] = authorized;
        await renderer.UpdateAsync(rootId, parameters);
        await renderer.InvokeButtonAsync(rootId, Variant.Text, "case-detail__correction-action");
        fields = renderer.FindComponents(rootId, typeof(MudTextField<string>)).Where(field => field.Parameters.ContainsKey(nameof(MudTextField<string>.Label))).ToArray();
        Assert.Equal("original narrative", fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Corrected report")).Parameters[nameof(MudTextField<string>.Value)]);
        await renderer.SetFieldValueAsync(fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Corrected report")), "sensitive draft");
        await renderer.SetFieldValueAsync(fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Reason for correction")), "sensitive reason");
        await renderer.InvokeButtonAsync(rootId, Variant.Filled, "case-detail__touch-action");

        Assert.NotEqual(Assert.IsType<CaseDetailCorrectionRequest>(first).CommandId,
            Assert.IsType<CaseDetailCorrectionRequest>(second).CommandId);
    }

    [Fact]
    public async Task PayloadBoundaryNewlinesDoNotReuseCommandId()
    {
        var services = CreateServices();
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var original = new CaseDetailCorrectionView(CaseDetailPanelState.Populated, 8, 2, CaseDetailCorrectionKind.LegacyV1, true, "original narrative");
        var commands = new List<CaseDetailCorrectionRequest>();
        var parameters = CreateParameters(Guid.NewGuid(), original,
            () => Task.FromResult<CaseDetailCorrectionView?>(original),
            request => { commands.Add(request); return Task.FromResult(new CaseDetailCorrectionOutcome(CaseDetailOperationStatus.Conflict)); });
        using var component = await renderer.RenderAsync(parameters);
        var rootId = component.ComponentId;
        await renderer.InvokeButtonAsync(rootId, Variant.Text, "case-detail__correction-action");
        var fields = renderer.FindComponents(rootId, typeof(MudTextField<string>)).Where(field => field.Parameters.ContainsKey(nameof(MudTextField<string>.Label))).ToArray();
        var narrative = fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Corrected report"));
        var reason = fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Reason for correction"));
        await renderer.SetFieldValueAsync(narrative, "part one\npart two");
        await renderer.SetFieldValueAsync(reason, "part three");
        await renderer.InvokeButtonAsync(rootId, Variant.Filled, "case-detail__touch-action");
        await renderer.SetFieldValueAsync(narrative, "part one");
        await renderer.SetFieldValueAsync(reason, "part two\npart three");
        await renderer.InvokeButtonAsync(rootId, Variant.Filled, "case-detail__touch-action");

        Assert.Equal(2, commands.Count);
        Assert.NotEqual(commands[0].CommandId, commands[1].CommandId);
    }

    [Fact]
    public async Task NavigationDuringCorrectionCommandDoesNotApplyOldResultToNewCase()
    {
        var services = CreateServices();
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var firstCaseId = Guid.NewGuid();
        var nextCaseId = Guid.NewGuid();
        var firstSnapshot = new CaseDetailCorrectionView(CaseDetailPanelState.Populated, 8, 2, CaseDetailCorrectionKind.LegacyV1, true, "first narrative");
        var nextSnapshot = new CaseDetailCorrectionView(CaseDetailPanelState.Populated, 4, 1, CaseDetailCorrectionKind.LegacyV1, true, "next narrative");
        var commandCompletion = new TaskCompletionSource<CaseDetailCorrectionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCalls = 0;
        var parameters = CreateParameters(firstCaseId, firstSnapshot,
            () => { refreshCalls++; return Task.FromResult<CaseDetailCorrectionView?>(firstSnapshot); },
            _ => { submitStarted.TrySetResult(); return commandCompletion.Task; });
        using var component = await renderer.RenderAsync(parameters);
        var rootId = component.ComponentId;
        await renderer.InvokeButtonAsync(rootId, Variant.Text, "case-detail__correction-action");
        var fields = renderer.FindComponents(rootId, typeof(MudTextField<string>)).Where(field => field.Parameters.ContainsKey(nameof(MudTextField<string>.Label))).ToArray();
        await renderer.SetFieldValueAsync(fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Corrected report")), "corrected");
        await renderer.SetFieldValueAsync(fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Reason for correction")), "reason");
        var submitTask = renderer.InvokeButtonAsync(rootId, Variant.Filled, "case-detail__touch-action");
        await submitStarted.Task;

        parameters[nameof(OperationsCaseDetailSurface.CaseId)] = nextCaseId;
        parameters[nameof(OperationsCaseDetailSurface.CorrectionData)] = nextSnapshot;
        await renderer.UpdateAsync(rootId, parameters);
        commandCompletion.SetResult(new(CaseDetailOperationStatus.Accepted, 9, 3));
        await submitTask;

        Assert.Equal(0, refreshCalls);
        Assert.All(renderer.FindComponents(rootId, typeof(MudAlert)), alert =>
            Assert.NotEqual(Severity.Success, alert.Parameters.GetValueOrDefault(nameof(MudAlert.Severity))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedCorrection_OnlyClosesFormAfterNewMatchingRevisionIsLoaded(bool matchingRefresh)
    {
        var services = CreateServices();
        await using var renderer = new TestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var original = new CaseDetailCorrectionView(CaseDetailPanelState.Populated, 8, 2, CaseDetailCorrectionKind.LegacyV1, true, "original narrative");
        var refreshed = matchingRefresh
            ? new CaseDetailCorrectionView(CaseDetailPanelState.Populated, 9, 3, CaseDetailCorrectionKind.LegacyV1, true, "revised narrative")
            : new CaseDetailCorrectionView(CaseDetailPanelState.Populated, 8, 2, CaseDetailCorrectionKind.LegacyV1, true, "original narrative");
        var refreshCalls = 0;
        var timeline = new CountingTimelineClient();
        CaseDetailCorrectionRequest? captured = null;
        var parameters = CreateParameters(Guid.NewGuid(), original,
            async () => { refreshCalls++; return refreshed; },
            request => { captured = request; return Task.FromResult(new CaseDetailCorrectionOutcome(CaseDetailOperationStatus.Accepted, 9, 3)); });
        parameters[nameof(OperationsCaseDetailSurface.TimelineClient)] = timeline;
        var component = await renderer.RenderAsync(parameters);
        using (component)
        {
            var surfaceId = component.ComponentId;
            await renderer.ActivateTabAsync(component.Instance, 1);
            await renderer.ActivateTabAsync(component.Instance, 0);
            await renderer.InvokeButtonAsync(surfaceId, Variant.Text, "case-detail__correction-action");
            var fields = renderer.FindComponents(surfaceId, typeof(MudTextField<string>)).Where(field => field.Parameters.ContainsKey(nameof(MudTextField<string>.Label))).ToArray();
            var timelineCallsBefore = timeline.GetCalls;
            await renderer.SetFieldValueAsync(fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Corrected report")), "revised narrative");
            await renderer.SetFieldValueAsync(fields.Single(field => Equals(field.Parameters[nameof(MudTextField<string>.Label)], "Reason for correction")), "source confirmed");
            await renderer.InvokeButtonAsync(surfaceId, Variant.Filled, "case-detail__touch-action");

            var submitted = Assert.IsType<CaseDetailCorrectionRequest>(captured);
            Assert.NotEqual(Guid.Empty, submitted.CommandId);
            Assert.Equal(8, submitted.ExpectedVersion);
            Assert.Equal("revised narrative", submitted.Narrative);
            Assert.Equal("source confirmed", submitted.Reason);
            Assert.Equal(1, refreshCalls);
            Assert.Equal(timelineCallsBefore + (matchingRefresh ? 1 : 0), timeline.GetCalls);
            var remainingCorrectionFields = renderer.FindComponents(surfaceId, typeof(MudTextField<string>))
                .Count(field => Equals(field.Parameters.GetValueOrDefault(nameof(MudTextField<string>.Label)), "Corrected report"));
            Assert.Equal(matchingRefresh ? 0 : 1, remainingCorrectionFields);
            if (!matchingRefresh)
            {
                Assert.NotEmpty(renderer.FindComponents(surfaceId, typeof(MudAlert)));
                var firstCommandId = submitted.CommandId;
                await renderer.InvokeButtonAsync(surfaceId, Variant.Filled, "case-detail__touch-action");
                Assert.Equal(firstCommandId, captured!.CommandId);
            }
            else
            {
                Assert.Contains(renderer.FindElements(surfaceId), element =>
                    Equals(element.GetValueOrDefault("aria-label"), "Correction result") &&
                    Equals(element.GetValueOrDefault("tabindex"), "-1"));
            }
        }
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, RecordingJsRuntime>();
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, object?> CreateParameters(Guid caseId, CaseDetailCorrectionView correction,
        Func<Task<CaseDetailCorrectionView?>> refresh, Func<CaseDetailCorrectionRequest, Task<CaseDetailCorrectionOutcome>> submit) => new()
    {
        [nameof(OperationsCaseDetailSurface.CaseId)] = caseId,
        [nameof(OperationsCaseDetailSurface.CorrectionData)] = correction,
        [nameof(OperationsCaseDetailSurface.RefreshCorrectionAsync)] = refresh,
        [nameof(OperationsCaseDetailSurface.SubmitCorrectionAsyncCallback)] = submit
    };

    private sealed class CountingTimelineClient : ICaseTimelineClient
    {
        public int GetCalls { get; private set; }
        public Task<CaseTimelineResult> GetAsync(Guid caseId, int? limit, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null));
        }
    }

    private sealed record ComponentParameters(int ComponentId, Type ComponentType, Dictionary<string, object?> Parameters);
    private sealed class TestRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public async Task<RenderedComponent> RenderAsync(CaseDetailPanelState state, CaseDetailCorrectionKind kind,
            bool canCorrect, Guid? caseId = null)
        {
            var narrative = canCorrect ? "original narrative" : null;
            var view = new CaseDetailCorrectionView(state, 8, 2, kind, canCorrect, narrative);
            var parameters = CreateParameters(caseId ?? Guid.NewGuid(), view,
                () => Task.FromResult<CaseDetailCorrectionView?>(view),
                _ => Task.FromResult(new CaseDetailCorrectionOutcome(CaseDetailOperationStatus.Failed)));
            return await RenderAsync(parameters);
        }

        public async Task<RenderedComponent> RenderAsync(Dictionary<string, object?> parameters)
        {
            var component = new OperationsCaseDetailSurface();
            var id = -1;
            await Dispatcher.InvokeAsync(async () =>
            {
                id = AssignRootComponentId(component);
                await RenderRootComponentAsync(id, ParameterView.FromDictionary(parameters));
            });
            return new RenderedComponent(this, id, component);
        }

        public Task UpdateAsync(int componentId, Dictionary<string, object?> parameters) => Dispatcher.InvokeAsync(() =>
            RenderRootComponentAsync(componentId, ParameterView.FromDictionary(parameters)));

        public Task ActivateTabAsync(object surface, int tabIndex) => Dispatcher.InvokeAsync(async () =>
        {
            var tabs = (MudTabs?)typeof(OperationsCaseDetailSurface)
                .GetField("_tabs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(surface);
            Assert.NotNull(tabs);
            await tabs!.ActivatePanelAsync(tabIndex, ignoreDisabledState: false);
        });

        public List<ComponentParameters> FindComponents(int componentId, Type type)
        {
            var found = new List<ComponentParameters>();
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType != RenderTreeFrameType.Component) continue;
                if (type.IsAssignableFrom(frame.ComponentType))
                {
                    var parameters = new Dictionary<string, object?>();
                    for (var parameterIndex = index + 1; parameterIndex < frames.Count && frames.Array[parameterIndex].FrameType == RenderTreeFrameType.Attribute; parameterIndex++)
                    {
                        var attribute = frames.Array[parameterIndex];
                        parameters[attribute.AttributeName] = attribute.AttributeValue;
                    }
                    found.Add(new ComponentParameters(frame.ComponentId, frame.ComponentType, parameters));
                }
                found.AddRange(FindComponents(frame.ComponentId, type));
            }
            return found;
        }

        public List<Dictionary<string, object?>> FindElements(int componentId)
        {
            var found = new List<Dictionary<string, object?>>();
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames.Array[index];
                if (frame.FrameType == RenderTreeFrameType.Element)
                {
                    var attributes = new Dictionary<string, object?>();
                    for (var attributeIndex = index + 1; attributeIndex < frames.Count && frames.Array[attributeIndex].FrameType == RenderTreeFrameType.Attribute; attributeIndex++)
                    {
                        var attribute = frames.Array[attributeIndex];
                        attributes[attribute.AttributeName] = attribute.AttributeValue;
                    }
                    found.Add(attributes);
                }
                else if (frame.FrameType == RenderTreeFrameType.Component)
                {
                    found.AddRange(FindElements(frame.ComponentId));
                }
            }
            return found;
        }

        public List<int> FindComponentIds(int componentId, Type type) =>
            FindComponents(componentId, type).Select(item => item.ComponentId).ToList();

        public async Task InvokeButtonAsync(int rootId, Variant variant, string? className)
        {
            var button = FindComponents(rootId, typeof(MudButton))
                .First(item => Equals(item.Parameters.GetValueOrDefault(nameof(MudButton.Variant)), variant) &&
                    Equals(item.Parameters.GetValueOrDefault(nameof(MudButton.Class)), className));
            var callback = Assert.IsType<EventCallback<MouseEventArgs>>(button.Parameters.GetValueOrDefault(nameof(MudButton.OnClick)));
            await Dispatcher.InvokeAsync(() => callback.InvokeAsync(new MouseEventArgs()));
        }

        public async Task SetFieldValueAsync(ComponentParameters field, string value)
        {
            var callback = Assert.IsType<EventCallback<string>>(field.Parameters.GetValueOrDefault(nameof(MudTextField<string>.ValueChanged)));
            await Dispatcher.InvokeAsync(() => callback.InvokeAsync(value));
        }

        public async Task SetBooleanValueAsync(ComponentParameters field, bool value)
        {
            var callback = Assert.IsType<EventCallback<bool>>(field.Parameters.GetValueOrDefault(nameof(MudCheckBox<bool>.ValueChanged)));
            await Dispatcher.InvokeAsync(() => callback.InvokeAsync(value));
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
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _calls = new();
        public IReadOnlyCollection<string> Calls => _calls.ToArray();
        public void Clear() { while (_calls.TryDequeue(out _)) { } }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            _calls.Enqueue(identifier);
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            _calls.Enqueue(identifier);
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
