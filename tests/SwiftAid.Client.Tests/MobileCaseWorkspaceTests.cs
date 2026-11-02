using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;
using SwiftAid.Mobile.Services.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class MobileCaseWorkspaceTests
{
    [Theory]
    [InlineData("HumanHealth", CaseWorkspaceSector.Human)]
    [InlineData("AnimalHealth", CaseWorkspaceSector.Animal)]
    [InlineData(null, CaseWorkspaceSector.Unknown)]
    [InlineData("Unrecognized", CaseWorkspaceSector.Unknown)]
    public async Task FiltersBeforePagingAndPreservesRecordedClassification(string? source, CaseWorkspaceSector sector)
    {
        var expected = Row(source, "Reported category");
        var client = new MobileCaseWorkspaceClient(new Queue(CaseQueueAccess.Allowed,
            [Row(source, "Different"), expected]));
        var result = await client.ListAsync(new(new PageRequest(1, 1), Sector: sector,
            ReportedCategoryKey: "reported category"));
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Value!.TotalCount);
        var row = Assert.Single(result.Value.Items);
        Assert.Equal(expected.CaseId, row.CaseId);
        Assert.Equal(sector, row.Sector);
        Assert.Equal("Reported category", row.ReportedCategoryLabel);
        Assert.False(result.Value.HasMore);
    }

    [Fact]
    public async Task FilterChoicesComeFromAuthorizedQueueOnly()
    {
        var client = new MobileCaseWorkspaceClient(new Queue(CaseQueueAccess.Allowed,
            [Row("AnimalHealth", "Recorded"), Row(null, null)]));
        var result = await client.GetFilterOptionsAsync();
        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Capabilities.Sector);
        Assert.True(result.Value.Capabilities.ReportedCategory);
        Assert.Equal("Recorded", Assert.Single(result.Value.ReportedCategories).Key);
        Assert.Contains(result.Value.Sectors, option => option.Key == "Unknown");
        Assert.DoesNotContain(result.Value.Sectors, option => option.Key == "Human");
    }

    [Theory]
    [InlineData(CaseQueueAccess.Denied, UiCapabilityResultCode.Denied)]
    [InlineData(CaseQueueAccess.Unavailable, UiCapabilityResultCode.Unavailable)]
    public async Task DeniedOrUnavailableQueueDoesNotExposeRowsOrChoices(CaseQueueAccess access, UiCapabilityResultCode expected)
    {
        var client = new MobileCaseWorkspaceClient(new Queue(access, [Row("HumanHealth", "Private")]));
        var rows = await client.ListAsync(new(new PageRequest()));
        var options = await client.GetFilterOptionsAsync();
        Assert.Equal(expected, rows.Code);
        Assert.Equal(expected, options.Code);
        Assert.Null(rows.Value);
        Assert.Null(options.Value);
    }

    private static CaseQueueItem Row(string? sector, string? classification) =>
        new(Guid.NewGuid(), "Reported", "SZ", "synthetic-owner", 1, DateTimeOffset.UtcNow,
            sector, classification);

    private sealed class Queue(CaseQueueAccess access, IReadOnlyList<CaseQueueItem> items) : ICaseQueueClient
    {
        public Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CaseQueueResult(access, items));
    }
}
