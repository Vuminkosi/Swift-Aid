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
