using SwiftAid.Client.Presentation;
using SwiftAid.Mobile.Services.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class MobileCaseWorkReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterCasePageMustMatchTheOriginalVersion(bool changeVersion)
    {
        var id = Guid.NewGuid();
        var results = await MobileCaseWorkReader.ReadAsync(new Client(changeVersion), [id],
            new WorkManagementQuery(new PageRequest(), WorkManagementKind.Assistance));
        var result = Assert.Single(results);
        if (changeVersion)
        {
            Assert.Null(result.Result);
        }
        else
        {
            Assert.True(result.Result!.Succeeded);
            Assert.Equal(101, result.Result.Value!.Items.Items.Count);
        }
    }

    private sealed class Client(bool changeVersion) : IWorkManagementClient
    {
        public Task<UiCapabilityResult<WorkManagementSnapshot>> GetAsync(Guid caseId, WorkManagementQuery query, CancellationToken cancellationToken = default)
        {
            var count = query.Page.PageNumber == 1 ? 100 : 1;
            var items = Enumerable.Range(0, count).Select(index => new WorkManagementItem($"{query.Page.PageNumber}-{index}", "Case", WorkManagementKind.Assistance,
                WorkManagementStatus.Open, WorkManagementAssignmentStatus.AssignedToYou, "Fixture", DateTimeOffset.UtcNow, null, [], [], [])).ToArray();
            var page = new Page<WorkManagementItem>(items, query.Page.PageNumber, 100, 101, query.Page.PageNumber == 1);
            return Task.FromResult(new UiCapabilityResult<WorkManagementSnapshot>(UiCapabilityResultCode.Success,
                new(caseId, changeVersion && query.Page.PageNumber == 2 ? 2 : 1, page),
                UiCapabilityResults.SyntheticMetadata("work", "Fixture")));
        }

        public Task<UiCapabilityResult<WorkManagementSnapshot>> ExecuteAsync(WorkManagementCommand command, WorkManagementQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
