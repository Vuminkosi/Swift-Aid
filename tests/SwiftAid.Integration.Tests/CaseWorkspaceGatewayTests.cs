using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class CaseWorkspaceGatewayTests
{
    [Theory]
    [InlineData("HumanHealth", CaseWorkspaceSector.Human)]
    [InlineData("AnimalHealth", CaseWorkspaceSector.Animal)]
    [InlineData(null, CaseWorkspaceSector.Unknown)]
    [InlineData("FutureSector", CaseWorkspaceSector.Unknown)]
    public async Task RecordedFiltersApplyBeforePagination(string? source, CaseWorkspaceSector sector)
    {
        var expected = Row(source, "Recorded");
        var client = new CaseWorkspaceGatewayClient(new Queue(CaseQueueAccess.Allowed,
            [Row(source, "Other"), expected]), new Metadata());
        var result = await client.ListAsync(new(new PageRequest(1, 1), Sector: sector, ReportedCategoryKey: "recorded"));
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Value!.TotalCount);
        Assert.Equal(expected.CaseId, Assert.Single(result.Value.Items).CaseId);
        Assert.Equal(sector, result.Value.Items[0].Sector);
        var options = await client.GetFilterOptionsAsync();
        Assert.True(options.Value!.Capabilities.Sector);
        Assert.True(options.Value.Capabilities.ReportedCategory);
        Assert.Equal(sector.ToString(), Assert.Single(options.Value.Sectors).Key);
        Assert.Contains(options.Value.ReportedCategories, option => option.Key == "Recorded");
    }

    [Theory]
    [InlineData(CaseQueueAccess.Denied, UiCapabilityResultCode.Denied)]
    [InlineData(CaseQueueAccess.Unavailable, UiCapabilityResultCode.Unavailable)]
    public async Task UnreadableQueueDoesNotExposeFilterOptions(CaseQueueAccess access, UiCapabilityResultCode code)
    {
        var client = new CaseWorkspaceGatewayClient(new Queue(access, [Row("HumanHealth", "Private")]), new Metadata());
        var rows = await client.ListAsync(new(new PageRequest(), Sector: CaseWorkspaceSector.Human));
        var options = await client.GetFilterOptionsAsync();
        Assert.Equal(code, rows.Code);
        Assert.Equal(code, options.Code);
        Assert.Null(rows.Value);
        Assert.Null(options.Value);
    }

    private static CaseQueueItem Row(string? sector, string classification) =>
        new(Guid.NewGuid(), "Reported", "SZ", "synthetic-owner", 1, DateTimeOffset.UtcNow, sector, classification);

    private sealed class Queue(CaseQueueAccess access, IReadOnlyList<CaseQueueItem> items) : ICaseQueueClient
    {
        public Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CaseQueueResult(access, items));
    }

    private sealed class Metadata : IPublicIntakeMetadataClient
    {
        public Task<UiCapabilityResult<PublicIntakeMetadata>> GetMetadataAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiCapabilityResult<PublicIntakeCountryCatalogue>> GetCountriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(UiCapabilityResults.Unavailable<PublicIntakeCountryCatalogue>("countries"));
    }
}
