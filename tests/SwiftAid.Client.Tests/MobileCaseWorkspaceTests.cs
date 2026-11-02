using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;
using SwiftAid.Mobile.Services.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class MobileCaseWorkspaceTests
{
    [Theory]
    [InlineData("Case abcd1234")]
    [InlineData("  ABCD1234  ")]
    public async Task DisplayedCaseReferenceCanBeSearchedWithoutExposingOwnerIdentifier(string search)
    {
        var target = Row("HumanHealth", "Recorded") with
        {
            CaseId = Guid.Parse("abcd1234-0000-0000-0000-000000000001"),
            AccountableOwnerActorId = "private-owner-identifier"
        };
        var other = target with { CaseId = Guid.Parse("ffff1234-0000-0000-0000-000000000002") };
        var client = new MobileCaseWorkspaceClient(new Queue(CaseQueueAccess.Allowed, [other, target]));
        var result = await client.ListAsync(new(new PageRequest(), Search: search));
        Assert.True(result.Succeeded);
        var row = Assert.Single(result.Value!.Items);
        Assert.Equal(target.CaseId, row.CaseId);
        Assert.Equal("Case abcd1234", row.PublicReference);
        Assert.Equal("Assigned to you", row.AssigneeLabel);
        Assert.DoesNotContain(target.AccountableOwnerActorId, System.Text.Json.JsonSerializer.Serialize(row));
        var missing = await client.ListAsync(new(new PageRequest(), Search: "Case 99999999"));
        Assert.Empty(missing.Value!.Items);
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
            var client = new MobileCaseWorkspaceClient(new Queue(CaseQueueAccess.Allowed, rows));
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
        var client = new MobileCaseWorkspaceClient(new Queue(CaseQueueAccess.Allowed,
            [Row("HumanHealth", "Recorded")]));
        var result = await client.ListAsync(new(new PageRequest(int.MaxValue, 100)));
        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Items);
        Assert.Equal(1, result.Value.TotalCount);
        Assert.False(result.Value.HasMore);
    }

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
