using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class CaseSummaryStateSeparationTests
{
    [Theory]
    [InlineData(LaboratoryState.NotRecorded, ReportedValueState.Unknown, "Not recorded", "Unknown")]
    [InlineData(LaboratoryState.Requested, ReportedValueState.NotRecorded, "Requested", "Not recorded")]
    [InlineData(LaboratoryState.Collected, ReportedValueState.NotApplicable, "Collected", "Not applicable")]
    [InlineData(LaboratoryState.Pending, ReportedValueState.Absent, "Pending", "Absent")]
    [InlineData(LaboratoryState.ResultRecorded, ReportedValueState.Known, "Result recorded", "Recorded")]
    [InlineData(null, null, "Unavailable", "Unavailable")]
    public void ResolutionPreservesSeparateLabAndOutcomeAvailability(LaboratoryState? laboratory,
        ReportedValueState? outcome, string expectedLab, string expectedOutcome)
    {
        var now = DateTimeOffset.UtcNow;
        var source = new CaseSummaryData(Guid.NewGuid(), CaseKind.Human, Sector.HumanHealth,
            ClinicalClassification.Suspected, CaseWorkflow.Resolved, "SZ", [], "owner", null, now, now,
            laboratory, outcome);
        var summary = CaseDetailSummaryMapper.ToSummary(source, new Dictionary<string, string>());
        Assert.NotNull(summary);
        Assert.True(summary.IsValid);
        Assert.Equal(expectedLab, summary.LaboratoryLabel);
        Assert.Equal(expectedOutcome, summary.OutcomeStateLabel);
        Assert.Equal("Suspected", summary.ClassificationLabel);
        Assert.Equal("Resolved", summary.Workflow);
    }

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
