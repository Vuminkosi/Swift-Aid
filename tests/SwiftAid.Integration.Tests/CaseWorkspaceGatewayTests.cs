using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class CaseWorkspaceGatewayTests
{
    [Theory]
    [InlineData(CaseQueueAccess.Allowed)]
    [InlineData(CaseQueueAccess.Denied)]
    public async Task IncrementalListPublishesAuthorizedFirstPageBeforeContinuationCompletes(CaseQueueAccess continuationAccess)
    {
        var first = Row("HumanHealth", "Recorded");
        var queue = new GatedQueue(first, continuationAccess);
        var client = new CaseWorkspaceGatewayClient(queue, new Metadata());
        var observed = new TaskCompletionSource<Page<CaseWorkspaceRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = client.ListIncrementallyAsync(new(new PageRequest(1, 25)), page =>
        {
            observed.TrySetResult(page);
            return Task.CompletedTask;
        });
        try
        {
            var partial = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(first.CaseId, Assert.Single(partial.Items).CaseId);
            Assert.False(loading.IsCompleted);
        }
        finally { queue.Release.TrySetResult(); }
        var final = await loading;
        Assert.Equal(continuationAccess == CaseQueueAccess.Allowed
            ? UiCapabilityResultCode.Success : UiCapabilityResultCode.Denied, final.Code);
        if (continuationAccess == CaseQueueAccess.Denied) Assert.Null(final.Value);
        else Assert.Equal(first.CaseId, Assert.Single(final.Value!.Items).CaseId);
    }

    private sealed class GatedQueue(CaseQueueItem first, CaseQueueAccess continuationAccess) : ICaseQueueClient
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default) =>
            GetMineAsync(limit, null, cancellationToken);
        public async Task<CaseQueueResult> GetMineAsync(int? limit, string? cursor, CancellationToken cancellationToken = default)
        {
            if (cursor is null) return new(CaseQueueAccess.Allowed, [first], "next");
            await Release.Task.WaitAsync(cancellationToken);
            return new(continuationAccess, []);
        }
    }

    [Theory]
    [InlineData(CaseWorkspaceSort.UpdatedNewestFirst)]
    [InlineData(CaseWorkspaceSort.UpdatedOldestFirst)]
    public async Task EqualTimestampsKeepStablePageMembershipAcrossQueueReordering(CaseWorkspaceSort sort)
    {
        var first = Row("HumanHealth", "Recorded") with { CaseId = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var second = first with { CaseId = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        foreach (var rows in new[] { new[] { second, first }, new[] { first, second } })
        {
            var client = new CaseWorkspaceGatewayClient(new Queue(CaseQueueAccess.Allowed, rows), new Metadata());
            var pageOne = await client.ListAsync(new(new PageRequest(1, 1), Sort: sort));
            var pageTwo = await client.ListAsync(new(new PageRequest(2, 1), Sort: sort));
            Assert.Equal(first.CaseId, Assert.Single(pageOne.Value!.Items).CaseId);
            Assert.Equal(second.CaseId, Assert.Single(pageTwo.Value!.Items).CaseId);
            Assert.True(pageOne.Value.HasMore);
            Assert.False(pageTwo.Value.HasMore);
        }
    }

    [Fact]
    public async Task LargePageCannotOverflowBackToFirstRows()
    {
        var client = new CaseWorkspaceGatewayClient(new Queue(CaseQueueAccess.Allowed,
            [Row("HumanHealth", "Recorded")]), new Metadata());
        var result = await client.ListAsync(new(new PageRequest(int.MaxValue, 100)));
        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Items);
        Assert.Equal(1, result.Value.TotalCount);
        Assert.False(result.Value.HasMore);
    }

    [Fact]
    public async Task WorkspaceLoadsEveryAuthorizedQueuePageBeforeFilteringAndPaging()
    {
        var rows = Enumerable.Range(0, 235)
            .Select(index => Row("HumanHealth", $"Category {index}") with
            {
                CaseId = Guid.Parse($"{index + 1:x8}-0000-0000-0000-000000000001"),
                UpdatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(235 - index)
            })
            .ToArray();
        var queue = new PagedQueue(rows);
        var client = new CaseWorkspaceGatewayClient(queue, new Metadata());

        var result = await client.ListAsync(new(new PageRequest(3, 50), Sector: CaseWorkspaceSector.Human));

        Assert.True(result.Succeeded);
        Assert.Equal(235, result.Value!.TotalCount);
        Assert.True(result.Value.HasMore);
        Assert.Equal(rows[100].CaseId, result.Value.Items[0].CaseId);
        var options = await client.GetFilterOptionsAsync();
        Assert.Contains(options.Value!.ReportedCategories, option => option.Key == "Category 234");
        Assert.Equal(6, queue.RequestedCursors.Count);
    }

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

    private sealed class PagedQueue(IReadOnlyList<CaseQueueItem> items) : ICaseQueueClient
    {
        public List<string?> RequestedCursors { get; } = [];

        public Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default) =>
            GetMineAsync(limit, null, cancellationToken);

        public Task<CaseQueueResult> GetMineAsync(int? limit, string? cursor, CancellationToken cancellationToken = default)
        {
            RequestedCursors.Add(cursor);
            var offset = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            var size = limit ?? 100;
            var page = items.Skip(offset).Take(size).ToArray();
            var nextOffset = offset + page.Length;
            var next = nextOffset < items.Count ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, page, next));
        }
    }

    private sealed class Metadata : IPublicIntakeMetadataClient
    {
        public Task<UiCapabilityResult<PublicIntakeMetadata>> GetMetadataAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiCapabilityResult<PublicIntakeCountryCatalogue>> GetCountriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(UiCapabilityResults.Unavailable<PublicIntakeCountryCatalogue>("countries"));
    }
}
