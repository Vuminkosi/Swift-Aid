using System.Text.Json;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiToolTraceTests
{
    [Fact]
    public void TraceRecordsReferencesAndHistoryWithoutExposingTraceInClientProjection()
    {
        var observation = new EllaiObservedToolResult("ellai_topic", true, "Ok",
            [new EvidenceRef("CaseQueue", "synthetic", "1", null, "proof")], false);
        var json = EllaiToolTrace.Serialize([observation], historyIncluded: true);
        Assert.NotNull(json);
        var trace = JsonSerializer.Deserialize<EllaiToolTrace>(json);
        Assert.True(trace!.HistoryIncluded);
        Assert.Equal("synthetic", Assert.Single(Assert.Single(trace.Observations).Evidence).ResourceId);
        var turn = new EllaiConversationTurn("session", "turn", 1, "question", "answer", "Completed", 1,
            DateTimeOffset.UtcNow) { ToolTraceJson = json };
        var clientJson = JsonSerializer.Serialize(EllaiConversationProjection.ToClient(turn));
        Assert.DoesNotContain("synthetic", clientJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTraceJson", clientJson, StringComparison.Ordinal);
    }

    [Fact]
    public void FlattenedTraceDeduplicatesEvidenceAndRejectsIncompleteOrExcessiveProvenance()
    {
        var observation = new EllaiObservedToolResult("tool", true, "Ok",
            [new EvidenceRef("CaseQueue", "synthetic", "1", null, "proof")], false);
        var json = EllaiToolTrace.SerializeFlattened([observation], true, [observation]);
        Assert.True(EllaiToolTrace.TryRead(json!, out var trace));
        Assert.Equal(3, trace!.Version);
        Assert.Single(trace.Observations);
        Assert.Null(EllaiToolTrace.SerializeFlattened(null, true, [observation]));
        var many = Enumerable.Range(0, 300).Select(index => observation with { ToolName = $"tool-{index}" }).ToArray();
        var expanded = EllaiToolTrace.SerializeFlattened([], true, many);
        Assert.True(EllaiToolTrace.TryRead(expanded!, out var restored));
        Assert.Equal(300, restored!.Observations.Count);
        var oversized = Enumerable.Range(0, 1_000).Select(index => observation with { ToolName = $"tool-{index}" }).ToArray();
        Assert.Null(EllaiToolTrace.SerializeFlattened([], true, oversized));
    }

    [Fact]
    public void ImportListObservationCapturesOnlyTheValidatedScopeKeys()
    {
        var request = new EllaiToolRequest(EllaiToolNames.Imports, new Dictionary<string, string>
        {
            ["workspace_id"] = "workspace", ["organization_id"] = "organization", ["country"] = "ZW", ["extra"] = "not retained"
        });
        var result = new EllaiToolResult(EllaiToolNames.Imports, true, null, [], "synthetic", 0, false);
        var observation = EllaiObservedToolResult.From(request, result);
        var json = EllaiToolTrace.SerializeFlattened([observation], false, []);
        Assert.True(EllaiToolTrace.TryRead(json!, out var trace));
        Assert.Equal(new EllaiImportListScope("workspace", "organization", "ZW"), Assert.Single(trace!.Observations).ImportListScope);
        Assert.DoesNotContain("not retained", json, StringComparison.Ordinal);
        Assert.Null(EllaiObservedToolResult.From(request, result with { Allowed = false }).ImportListScope);
    }

    [Fact]
    public void CaseQueueObservationCapturesSupportedQueryOptions()
    {
        var request = new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>
        {
            ["projection"] = "queue", ["limit"] = "10", ["country"] = "ZW", ["workflow"] = "Reported", ["sort"] = "latest-reported"
        });
        var result = new EllaiToolResult(EllaiToolNames.State, true, null, [], "synthetic", 0, false);
        var json = EllaiToolTrace.SerializeFlattened([EllaiObservedToolResult.From(request, result)], false, []);
        Assert.True(EllaiToolTrace.TryRead(json!, out var trace));
        Assert.Equal(new EllaiCaseListQuery(10, "ZW", "Reported", "latest-reported"), Assert.Single(trace!.Observations).CaseListQuery);
    }

    [Fact]
    public void UnknownMalformedAndOversizedTracesAreNotAcceptedAsProvenance()
    {
        Assert.Null(EllaiToolTrace.Serialize(null, false));
        Assert.False(EllaiToolTrace.IsValid("{}"));
        Assert.False(EllaiToolTrace.IsValid("not json"));
        var large = new EllaiObservedToolResult("tool", true, "Ok",
            [new EvidenceRef("CaseQueue", new string('x', 60_001), "1", null, "proof")], false);
        Assert.Null(EllaiToolTrace.Serialize([large], false));
        Assert.Throws<ArgumentException>(() => (EllaiUtteranceResolution.Failed with { ToolTraceJson = "{}" }).Validate());
    }
}
