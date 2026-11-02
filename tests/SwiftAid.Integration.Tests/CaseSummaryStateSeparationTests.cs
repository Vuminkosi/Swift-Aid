using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class CaseSummaryStateSeparationTests
{
    public static IEnumerable<object[]> StatePairs() =>
        from workflow in Enum.GetValues<CaseWorkflow>()
        from classification in Enum.GetValues<ClinicalClassification>()
        select new object[] { workflow, classification };

    [Theory]
    [MemberData(nameof(StatePairs))]
    public void WorkflowDoesNotInferClassificationOrOperationalPriority(CaseWorkflow workflow, ClinicalClassification classification)
    {
        var now = DateTimeOffset.UtcNow;
        var source = new CaseSummaryData(Guid.NewGuid(), CaseKind.Human, Sector.HumanHealth,
            classification, workflow, "SZ", [], "synthetic-owner", "Synthetic worker", now, now);
        var summary = CaseDetailSummaryMapper.ToSummary(source, new Dictionary<string, string> { ["SZ"] = "Eswatini" });
        Assert.NotNull(summary);
        Assert.True(summary.IsValid);
        Assert.Equal(workflow.ToString(), summary.Workflow);
        Assert.Equal(classification.ToString(), summary.ClassificationLabel);
        Assert.Equal("Not assessed", summary.PriorityLabel);
    }
}
