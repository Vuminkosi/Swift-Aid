using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T07")]
public sealed class EllaiReadToolTests
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true);

    private static EllaiToolBroker GrantedBroker()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, "synthetic", TimeSpan.FromHours(1));
        return new EllaiToolBroker(new EllaiPolicyService(), consent);
    }

    [Fact]
    public async Task NestedCallsCannotBypassBoundOrScope()
    {
        var broker = GrantedBroker();

        var unregistered = await broker.InvokeAsync(
            Context, new EllaiToolRequest("ellai_sql", new Dictionary<string, string>()),
            CancellationToken.None);
        Assert.False(unregistered.Allowed);

        var exhausted = await broker.InvokeAsync(
            Context with { RemainingCalls = 0 },
            new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()),
            CancellationToken.None);
        Assert.False(exhausted.Allowed);
        Assert.Contains("budget", exhausted.DenialReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var codeArg = await broker.InvokeAsync(
            Context,
            new EllaiToolRequest(EllaiToolNames.Evidence,
                new Dictionary<string, string> { ["q"] = "SELECT * FROM cases" }),
            CancellationToken.None);
        Assert.False(codeArg.Allowed);
    }

    [Fact]
    public async Task RegisteredReadsReturnBoundedResults()
    {
        var docsPath = Path.Combine(Path.GetTempPath(), $"technical-{Guid.NewGuid():N}.md");
        File.WriteAllText(docsPath, "## 1. Sample\n\n**Status: Implemented.** Sample section.\n");
        try
        {
            var consent = new EllaiConsentService();
            consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
                EllaiToolBroker.ToolReadPurpose, "synthetic", TimeSpan.FromHours(1));
            var topics = new EllaiTopicService();
            await topics.TrackAsync("session-1", "Sample topic", new[] { "sample" }, null, null);
            var registry = new EllaiUiRegistry();
            registry.Register(new UiControlDescriptor(
                "cases.sample", "cases", 1, "ellai-uicontract/1.0", "Cases",
                "Button", "label-key", "desc-key",
                Array.Empty<string>(), new[] { "sample" }, Array.Empty<string>(),
                "nav-cases", "anchor-sample",
                new[] { "HighlightControl" }, UiRiskClass.R0,
                new[] { GuidanceMode.Navigation }, Array.Empty<string>(), Array.Empty<string>(),
                "sample", "proof-ui-observed",
                Array.Empty<string>(), "internal", false));
            var observations = new InMemoryEllaiUiObservationStore();
            await observations.RecordAsync(new StoredUiObservationReport(
                "session-1", "view-1", "ctx-1", "web",
                new[] { "cases.sample" }, DateTimeOffset.UtcNow), CancellationToken.None);
            var broker = new EllaiToolBroker(
                new EllaiPolicyService(), consent, new EllaiTechnicalDocs(docsPath), null,
                new EllaiReceiptStore(), topics, registry,
                new EllaiUiDiscoveryService(), observations);

            foreach (var tool in EllaiToolNames.All)
            {
                var args = tool == EllaiToolNames.DocsSearch
                    ? new Dictionary<string, string> { ["q"] = "sample" }
                    : tool == EllaiToolNames.DocsGet
                        ? new Dictionary<string, string> { ["section"] = "1-sample" }
                        : tool == EllaiToolNames.Topic
                            ? new Dictionary<string, string> { ["ref"] = "sample" }
                            : tool == EllaiToolNames.UiObserve
                                ? new Dictionary<string, string>
                                {
                                    ["controls"] = "cases.sample",
                                    ["view"] = "view-1",
                                }
                                : new Dictionary<string, string>();
                var result = await broker.InvokeAsync(
                    Context, new EllaiToolRequest(tool, args),
                    CancellationToken.None);
                Assert.True(result.Allowed);
                Assert.Equal(EllaiToolReasonCodes.Ok, result.ReasonCode);
                Assert.True(result.ItemCount <= EllaiToolBroker.MaxItems);
                Assert.True(EllaiToolBroker.MeasureEnvelopeBytes(result) <= EllaiToolBroker.MaxResultBytes);
            }
        }
        finally
        {
            File.Delete(docsPath);
        }
    }

    [Fact]
    public async Task MissingScopeIsDenied()
    {
        var broker = GrantedBroker();

        var result = await broker.InvokeAsync(
            Context with { ScopeEpoch = string.Empty },
            new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.False(result.Allowed);
    }
}
