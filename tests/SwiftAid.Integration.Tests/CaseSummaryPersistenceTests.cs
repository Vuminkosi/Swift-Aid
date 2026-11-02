using Microsoft.EntityFrameworkCore;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class CaseSummaryPersistenceTests
{
    [IntakePostgresFact]
    public async Task FreshContextReadsIndependentLaboratoryAndOutcomeStateAfterResolution()
    {
        Guid caseId;
        await using (var write = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync())
        {
            caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(write);
            await write.ManagedCases.Where(item => item.CaseId == caseId).ExecuteUpdateAsync(set => set
                .SetProperty(item => item.Workflow, CaseWorkflow.Resolved)
                .SetProperty(item => item.Classification, ClinicalClassification.Suspected)
                .SetProperty(item => item.Laboratory, LaboratoryState.Pending)
                .SetProperty(item => item.OutcomeState, ReportedValueState.Unknown));
        }

        await using var read = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var stored = await new PostgresCaseSummaryStore(read).GetAsync(caseId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(CaseWorkflow.Resolved, stored.Workflow);
        Assert.Equal(ClinicalClassification.Suspected, stored.Classification);
        Assert.Equal(LaboratoryState.Pending, stored.Laboratory);
        Assert.Equal(ReportedValueState.Unknown, stored.OutcomeState);
        var summary = CaseDetailSummaryMapper.ToSummary(stored, new Dictionary<string, string>());
        Assert.NotNull(summary);
        Assert.Equal("Pending", summary.LaboratoryLabel);
        Assert.Equal("Unknown", summary.OutcomeStateLabel);
        Assert.Equal("Not assessed", summary.PriorityLabel);
        Assert.Null(await new PostgresCaseSummaryStore(read).GetAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
