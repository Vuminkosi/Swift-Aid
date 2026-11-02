using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiGuidanceCheckpointTests
{
    [Fact]
    public void RoundTripRetainsPlanPositionAndEvidenceWithoutAllowingUnknownFields()
    {
        var checkpoint = Sample();
        var json = EllaiGuidanceCheckpoint.Serialize(checkpoint);
        var restored = EllaiGuidanceCheckpoint.Deserialize(json)!;
        Assert.Equal("step-1", restored.CurrentStepId);
        Assert.Equal("plan-1", restored.Plan!.PlanId);
        Assert.Equal("report", restored.NavigationId);
        Assert.Empty(restored.CompletedStepIds);
        Assert.Throws<System.Text.Json.JsonException>(() => EllaiGuidanceCheckpoint.Deserialize(json[..^1] + ",\"secret\":\"value\"}"));
    }

    [Fact]
    public void CheckpointRejectsUnboundedOrInconsistentProgress()
    {
        Assert.Throws<ArgumentException>(() => EllaiGuidanceCheckpoint.Serialize(Sample() with { CurrentStepId = "not-in-plan" }));
        Assert.Throws<ArgumentException>(() => EllaiGuidanceCheckpoint.Serialize(Sample() with { CompletedStepIds = ["not-in-plan"] }));
        Assert.Throws<ArgumentException>(() => EllaiGuidanceCheckpoint.Serialize(Sample() with { Status = "MadeUp" }));
        Assert.Throws<ArgumentException>(() => EllaiGuidanceCheckpoint.Serialize(Sample() with { ContextStamp = new string('x', 100_000) }));
    }

    internal static EllaiGuidanceCheckpoint Sample() => new(
        "proposal", "context", "scope", 1, 1, "generation", "Ready", DateTimeOffset.UtcNow,
        new("plan-1", 1, null, "Initial acceptance", ["step-1"], [], [],
            [new("step-1", "report.entry", "report.open", PlanStepBehavior.Explain, null, null, [], [], "Open the report form.", null)],
            [], "catalogue", "contract"), "step-1", [], [], "report");
}
