using SwiftAid.Client.Presentation;
using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Intake;
using SwiftAid.Mobile.Services.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class MobileCaseWorkspaceTests
{
    [Theory]
    [InlineData("Eswatini")]
    [InlineData("Under review")]
    public async Task SearchesDisplayedCountryAndWorkflowLabels(string search)
    {
        var row = Row("HumanHealth", "Recorded") with { Workflow = "UnderReview" };
        var client = new MobileCaseWorkspaceClient(new Queue(CaseQueueAccess.Allowed, [row]), null, new Countries());
        var result = await client.ListAsync(new(new PageRequest(), Search: search));
        Assert.Equal("Eswatini", Assert.Single(result.Value!.Items).CountryLabel);
        var options = (await client.GetFilterOptionsAsync()).Value!;
        Assert.Contains(options.Countries, item => item.Key == "SZ" && item.Label == "Eswatini");
        Assert.Contains(options.Workflows, item => item.Key == "UnderReview" && item.Label == "Under review");
    }

    [Fact]
    public async Task MissingCountryCataloguePreservesTheAuthorizedCaseList()
    {
        var row = Row("HumanHealth", "Recorded");
        var client = new MobileCaseWorkspaceClient(new Queue(CaseQueueAccess.Allowed, [row]), null, new Countries(fail: true));
        var result = await client.ListAsync(new(new PageRequest()));
        Assert.True(result.Succeeded);
        Assert.Equal("SZ", Assert.Single(result.Value!.Items).CountryLabel);
    }

    [Fact]
    public async Task VisibleRowsIncludeAuthorizedReportDetailsAndMapLink()
    {
        var row = Row("HumanHealth", "Recorded");
        var place = new CaseReportPlace("SZ", "Example region", "Example area", null, "region", "area", null);
        var reports = new Reports(new CaseReportDetails(row.CaseId, 1, "Synthetic narrative", ["Synthetic observation"], place));
        var client = new MobileCaseWorkspaceClient(new Queue(CaseQueueAccess.Allowed, [row]), reports);

        var result = await client.ListAsync(new(new PageRequest()));

        var item = Assert.Single(result.Value!.Items);
        Assert.Equal("Synthetic observation", item.ObservationPreview);
        Assert.Equal("Example region | Example area", item.RegionLabel);
        Assert.Contains("country=SZ", item.LocationHref);
        Assert.Equal(row.CaseId, Assert.Single(reports.Requested));
    }

    [Fact]
    public async Task RevokedCaseDetailsDoNotExposeAStaleQueue()
    {
        var row = Row("HumanHealth", "Recorded");
        var client = new MobileCaseWorkspaceClient(new Queue(CaseQueueAccess.Allowed, [row]), new Reports(null, denied: true));
        var result = await client.ListAsync(new(new PageRequest()));
        Assert.Equal(UiCapabilityResultCode.Denied, result.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task MismatchedReportDoesNotAppearInAnotherCaseRow()
    {
        var row = Row("HumanHealth", "Recorded");
        var wrong = new CaseReportDetails(Guid.NewGuid(), 1, "Other case", [], null);
        var client = new MobileCaseWorkspaceClient(new Queue(CaseQueueAccess.Allowed, [row]), new Reports(wrong));
        var item = Assert.Single((await client.ListAsync(new(new PageRequest()))).Value!.Items);
        Assert.Null(item.ObservationPreview);
        Assert.Null(item.LocationHref);
    }

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

    [Fact]
    public async Task MobileLoadsEveryAuthorizedQueuePageBeforeFilteringAndPaging()
    {
        var rows = Enumerable.Range(0, 535)
            .Select(index => Row("HumanHealth", $"Category {index}") with
            {
                CaseId = Guid.Parse($"{index + 1:x8}-0000-0000-0000-000000000001"),
                UpdatedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(535 - index)
            })
            .ToArray();
        var queue = new PagedQueue(rows);
        var client = new MobileCaseWorkspaceClient(queue);

        var result = await client.ListAsync(new(new PageRequest(6, 50), Sector: CaseWorkspaceSector.Human));

        Assert.True(result.Succeeded);
        Assert.Equal(535, result.Value!.TotalCount);
        Assert.True(result.Value.HasMore);
        Assert.Equal(rows[250].CaseId, result.Value.Items[0].CaseId);
        var options = await client.GetFilterOptionsAsync();
        Assert.Contains(options.Value!.ReportedCategories, option => option.Key == "Category 534");
        Assert.Equal(12, queue.RequestedCursors.Count);
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

    private sealed class Countries(bool fail = false) : IGuestIntakeTransport
    {
        public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken cancellationToken) => fail
            ? Task.FromException<PublicIntakeCountryCatalogue>(new HttpRequestException())
            : Task.FromResult(new PublicIntakeCountryCatalogue("v1", "/countries", [new("SZ", "Eswatini"), new("ZA", "South Africa")]));
        public Task<PublicIntakeMetadata> GetMetadataAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IntakeSessionResponse> CreateSessionAsync(Guid sessionId, string receiptProof, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IntakeReceipt> SubmitAsync(IntakeSubmission submission, Guid sessionId, string intakeCapability, string receiptProof, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IntakeReceipt> GetReceiptAsync(Guid receiptId, string receiptProof, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Reports(CaseReportDetails? details, bool denied = false) : ICaseReportDetailsClient
    {
        public List<Guid> Requested { get; } = [];
        public Task<UiCapabilityResult<CaseReportDetails>> GetAsync(Guid caseId, CancellationToken ct = default)
        {
            Requested.Add(caseId);
            return Task.FromResult(new UiCapabilityResult<CaseReportDetails>(
                denied ? UiCapabilityResultCode.Denied : UiCapabilityResultCode.Success, details,
                new UiCapabilityMetadata("case-detail", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true)));
        }
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
}
