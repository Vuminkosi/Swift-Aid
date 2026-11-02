using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T07")]
public sealed class EllaiUiToolTests
{
    [Theory]
    [InlineData(0, "True")]
    [InlineData(60, "False")]
    public async Task ObservationExposesOnlyFreshPolicyEligibleValidationEvidence(int age, string accepted)
    {
        var registry = SeededRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var store = new InMemoryEllaiUiObservationStore();
        await store.RecordAsync(new("session-1", "view", "context", "web", ["report.entry"],
            DateTimeOffset.UtcNow.AddSeconds(-age), [new("report.entry", UiObservationState.Ready)],
            CompletedControlIds: ["report.subject", "report.submit"]), default);
        var result = await GrantedBroker(registry, store).InvokeAsync(Context with { FeatureId = "public.product-help" },
            new(EllaiToolNames.UiObserve, Args(("controls", "report.subject,report.submit"), ("view", "current"))), default);
        Assert.True(result.Allowed);
        Assert.EndsWith("client form validation accepted " + accepted,
            result.ResultSummary.Split('\n').Single(line => line.StartsWith("control report.subject", StringComparison.Ordinal)));
        Assert.EndsWith("client form validation accepted False",
            result.ResultSummary.Split('\n').Single(line => line.StartsWith("control report.submit", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(46, "view-9", UiObservationState.Ready, "Stale")]
    [InlineData(0, "other", UiObservationState.Ready, "Stale")]
    [InlineData(-10, "view-9", UiObservationState.Ready, "Stale")]
    [InlineData(0, "view-9", UiObservationState.Hidden, "Hidden")]
    [InlineData(0, "view-9", UiObservationState.Disabled, "Disabled")]
    public async Task ObserveNeverUpgradesStaleHiddenOrDisabledControls(int age, string view, UiObservationState state, string expected)
    {
        var store = new InMemoryEllaiUiObservationStore();
        await store.RecordAsync(new("session-1", "view-9", "ctx", "web", ["cases.queue"],
            DateTimeOffset.UtcNow.AddSeconds(-age), [new("cases.queue", state)]), CancellationToken.None);
        var result = await GrantedBroker(SeededRegistry(), store).InvokeAsync(Context,
            new(EllaiToolNames.UiObserve, Args(("controls", "cases.queue"), ("view", view))), CancellationToken.None);
        Assert.Contains(expected, result.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryConversationDiscoversPublicReportButCannotSelectPrivateFeatures()
    {
        var registry = SeededRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var broker = GrantedBroker(registry);
        var context = Context with { FeatureId = "public.product-help", ResourceId = null,
            ConsentPurpose = EllaiConsentService.PublicProductHelpPurpose,
            DataClassification = EllaiProcessingClassification.UserProvidedNonSensitive,
            AllowedTools = new HashSet<string> { EllaiToolNames.UiCatalogue, EllaiToolNames.UiObserve } };
        var result = await broker.InvokeAsync(context, new(EllaiToolNames.UiCatalogue, Args()), default);
        Assert.True(result.Allowed);
        Assert.Contains("report.entry", result.ResultSummary);
        Assert.Contains("feature report", result.ResultSummary);
        Assert.Contains("purpose Open the public report form.", result.ResultSummary);
        Assert.DoesNotContain("cases.queue", result.ResultSummary);
        Assert.DoesNotContain("guided-report-title", result.ResultSummary);
        var denied = await broker.InvokeAsync(context, new(EllaiToolNames.UiCatalogue, Args(("feature", "cases"))), default);
        Assert.False(denied.Allowed);
    }

    [Theory]
    [InlineData(0, "Ready")]
    [InlineData(60, "Stale")]
    public async Task CurrentViewUsesStoredIdentityWithoutDiscardingFreshnessChecks(int age, string expected)
    {
        var registry = SeededRegistry();
        EllaiReportGuidanceDescriptors.RegisterAll(registry);
        var observations = new InMemoryEllaiUiObservationStore();
        await observations.RecordAsync(new("session-1", "actual-view", "context", "web", ["report.entry"],
            DateTimeOffset.UtcNow.AddSeconds(-age), [new("report.entry", UiObservationState.Ready)]), default);
        var context = Context with { FeatureId = "public.product-help", ResourceId = null };
        var result = await GrantedBroker(registry, observations).InvokeAsync(context,
            new(EllaiToolNames.UiObserve, Args(("controls", "report.entry"), ("view", "current"))), default);
        Assert.True(result.Allowed);
        Assert.Contains(expected, result.ResultSummary);
        Assert.Contains("actual-view", result.ResultSummary);
        var denied = await GrantedBroker(registry, observations).InvokeAsync(context,
            new(EllaiToolNames.UiObserve, Args(("controls", "cases.queue"), ("view", "current"))), default);
        Assert.False(denied.Allowed);
    }

    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true, "consent/1", EllaiProcessingClassification.SyntheticDemo);

    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static UiControlDescriptor Descriptor(
        string control, string feature = "cases", string[]? requiredCapabilities = null,
        string[]? goalTags = null) => new(
        control, feature, 1, "ellai-uicontract/1.0", "Cases",
        "Button", "label-key", "desc-key",
        Array.Empty<string>(), goalTags ?? new[] { "view-queue" }, Array.Empty<string>(),
        "nav-cases", "anchor-queue",
        new[] { "HighlightControl" }, UiRiskClass.R0,
        new[] { GuidanceMode.Navigation }, Array.Empty<string>(), Array.Empty<string>(),
        "view-queue", "proof-ui-observed",
        requiredCapabilities ?? Array.Empty<string>(), "internal", false);

    private static EllaiToolBroker GrantedBroker(
        EllaiUiRegistry? registry = null,
        IEllaiUiObservationStore? observations = null)
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        return new EllaiToolBroker(
            new EllaiPolicyService(), consent, null, null, null, null,
            registry, new EllaiUiDiscoveryService(), observations);
    }

    private static EllaiUiRegistry SeededRegistry()
    {
        var registry = new EllaiUiRegistry();
        registry.Register(Descriptor("cases.queue"));
        registry.Register(Descriptor("cases.summary", goalTags: new[] { "view-summary" }));
        registry.Register(Descriptor("cases.refunds", requiredCapabilities: new[] { "cases.refund" }));
        registry.Register(Descriptor("billing.overview", "billing"));
        return registry;
    }

    [Fact]
    public async Task CatalogueReturnsEntitledSliceWithoutSecrets()
    {
        var broker = GrantedBroker(SeededRegistry());

        var result = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.UiCatalogue, Args()),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Ok, result.ReasonCode);
        Assert.Equal(2, result.ItemCount);
        Assert.Contains("ellai-catalogue/1.0", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("cases.queue", result.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("cases.refunds", result.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("billing.overview", result.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownGoalDoesNotMisreportMissingEntitlement()
    {
        var result = await GrantedBroker(SeededRegistry()).InvokeAsync(Context,
            new(EllaiToolNames.UiCatalogue, Args(("goal", "explain this screen"))), CancellationToken.None);
        Assert.True(result.Allowed);
        Assert.Equal(2, result.ItemCount);
        Assert.Contains("No exact goal tag matched", result.ResultSummary);
        Assert.Contains("view-summary", result.ResultSummary);
        Assert.DoesNotContain("cases.refunds", result.ResultSummary);
        Assert.DoesNotContain("billing.overview", result.ResultSummary);
    }

    [Fact]
    public async Task CatalogueGoalFilterNarrowsToTag()
    {
        var broker = GrantedBroker(SeededRegistry());

        var result = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.UiCatalogue, Args(("goal", "view-summary"))),
            CancellationToken.None);

        Assert.True(result.Allowed);
        var single = Assert.Single(result.Evidence);
        Assert.Equal("cases.summary", single.ResourceId);
    }

    [Fact]
    public async Task CatalogueRejectsBadLimit()
    {
        var broker = GrantedBroker(SeededRegistry());

        var result = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.UiCatalogue, Args(("limit", "99"))),
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, result.ReasonCode);
    }

    [Fact]
    public async Task CatalogueIsUnavailableWithoutRegistry()
    {
        var broker = GrantedBroker();

        var result = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.UiCatalogue, Args()),
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, result.ReasonCode);
    }

    [Fact]
    public async Task ObserveMarksMountedReadyAndOthersUnmounted()
    {
        var store = new InMemoryEllaiUiObservationStore();
        await store.RecordAsync(new StoredUiObservationReport(
            "session-1", "view-9", "ctx-9", "web",
            new[] { "cases.queue" }, DateTimeOffset.UtcNow, [new("cases.queue", UiObservationState.Ready)]), CancellationToken.None);
        var broker = GrantedBroker(SeededRegistry(), store);

        var result = await broker.InvokeAsync(
            Context,
            new EllaiToolRequest(EllaiToolNames.UiObserve,
                Args(("controls", "cases.queue, cases.summary"), ("view", "view-9"))),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(2, result.ItemCount);
        Assert.Contains("ClientObservation", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("view-9", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("cases.queue · Ready", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("cases.summary · Unmounted", result.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveNotesViewMismatchHonestly()
    {
        var store = new InMemoryEllaiUiObservationStore();
        await store.RecordAsync(new StoredUiObservationReport(
            "session-1", "view-9", "ctx-9", "web",
            new[] { "cases.queue" }, DateTimeOffset.UtcNow, [new("cases.queue", UiObservationState.Ready)]), CancellationToken.None);
        var broker = GrantedBroker(SeededRegistry(), store);

        var result = await broker.InvokeAsync(
            Context,
            new EllaiToolRequest(EllaiToolNames.UiObserve,
                Args(("controls", "cases.queue"), ("view", "view-other"))),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Contains("differs from the latest report", result.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveFailsClosedOnUnknownControl()
    {
        var store = new InMemoryEllaiUiObservationStore();
        await store.RecordAsync(new StoredUiObservationReport(
            "session-1", "view-9", "ctx-9", "web",
            new[] { "cases.queue" }, DateTimeOffset.UtcNow, [new("cases.queue", UiObservationState.Ready)]), CancellationToken.None);
        var broker = GrantedBroker(SeededRegistry(), store);

        var result = await broker.InvokeAsync(
            Context,
            new EllaiToolRequest(EllaiToolNames.UiObserve,
                Args(("controls", "cases.queue, admin.purge"), ("view", "view-9"))),
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, result.ReasonCode);
        Assert.Contains("admin.purge", result.DenialReason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveWithoutReportIsAnAuthorizedEmpty()
    {
        var broker = GrantedBroker(SeededRegistry(), new InMemoryEllaiUiObservationStore());

        var result = await broker.InvokeAsync(
            Context,
            new EllaiToolRequest(EllaiToolNames.UiObserve,
                Args(("controls", "cases.queue"), ("view", "view-9"))),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(0, result.ItemCount);
        Assert.Contains("No client UI observation", result.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveRequiresControlsAndView()
    {
        var broker = GrantedBroker(SeededRegistry(), new InMemoryEllaiUiObservationStore());

        var missingControls = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.UiObserve, Args(("view", "view-9"))),
            CancellationToken.None);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, missingControls.ReasonCode);

        var missingView = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.UiObserve, Args(("controls", "cases.queue"))),
            CancellationToken.None);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, missingView.ReasonCode);
    }

    [Fact]
    public async Task ObserveIsUnavailableWithoutSources()
    {
        var broker = GrantedBroker();

        var result = await broker.InvokeAsync(
            Context,
            new EllaiToolRequest(EllaiToolNames.UiObserve,
                Args(("controls", "cases.queue"), ("view", "view-9"))),
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, result.ReasonCode);
    }

    [Fact]
    public async Task ObservationReportsStaySessionScoped()
    {
        var store = new InMemoryEllaiUiObservationStore();
        await store.RecordAsync(new StoredUiObservationReport(
            "session-1", "view-1", "ctx-1", "web",
            new[] { "cases.queue" }, DateTimeOffset.UtcNow, [new("cases.queue", UiObservationState.Ready)]), CancellationToken.None);
        await store.RecordAsync(new StoredUiObservationReport(
            "session-2", "view-2", "ctx-2", "web",
            new[] { "cases.summary" }, DateTimeOffset.UtcNow), CancellationToken.None);

        var first = await store.LatestAsync("session-1", CancellationToken.None);
        var second = await store.LatestAsync("session-2", CancellationToken.None);
        var missing = await store.LatestAsync("session-3", CancellationToken.None);

        Assert.Equal("view-1", first?.ViewInstanceId);
        Assert.Equal("view-2", second?.ViewInstanceId);
        Assert.Null(missing);
    }
}
