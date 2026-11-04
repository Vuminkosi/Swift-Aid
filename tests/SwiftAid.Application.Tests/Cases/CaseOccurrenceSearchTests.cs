using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Tests.Cases;

public sealed class CaseOccurrenceSearchTests
{
    private static ActorContext Actor() => ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "officer"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
    private static CaseQueueItem Row(int id) => new(new Guid(id, 0, 0, new byte[8]), "Reported", "SZ", "officer", 1, DateTimeOffset.UtcNow);

    private sealed class Queue : ICaseQueueQuery
    {
        public Queue(params CaseQueueResult[] pages) => Pages = new(pages);
        public Queue<CaseQueueResult> Pages { get; }
        public List<string?> Cursors { get; } = [];
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<CaseQueueResult> GetSearchableAsync(ActorContext actor, int? limit, CaseQueueFilter? filter, string? cursor, CancellationToken ct)
        {
            Assert.Equal("SZ", filter!.CountryCode);
            Assert.True(filter.NewestReportedFirst);
            Cursors.Add(cursor);
            return Task.FromResult(Pages.Dequeue());
        }
    }

    private sealed class Locations : ICaseOccurrenceLocationQuery
    {
        public Dictionary<Guid, CaseOccurrenceLocationResult> Results { get; } = [];
        public Task<CaseOccurrenceLocationResult> GetLocationAsync(ActorContext actor, Guid caseId, CancellationToken ct) => Task.FromResult(Results[caseId]);
    }

    [Fact]
    public async Task ReadsAcrossPagesAndReportsUnknownAndExtraMatches()
    {
        var first = Row(1); var second = Row(2); var third = Row(3); var fourth = Row(4);
        var queue = new Queue(new(CaseQueueAccess.Allowed, [first, second], "next"), new(CaseQueueAccess.Allowed, [third, fourth]));
        var locations = new Locations();
        locations.Results[first.CaseId] = new(CaseOccurrenceLocationAccess.Allowed, 1, null);
        locations.Results[second.CaseId] = new(CaseOccurrenceLocationAccess.Allowed, 2, new("SZ", "Manzini", null, null, "region-1", null, null));
        locations.Results[third.CaseId] = new(CaseOccurrenceLocationAccess.NotFoundOrNotAuthorized, null, null);
        locations.Results[fourth.CaseId] = new(CaseOccurrenceLocationAccess.Allowed, 4, new("SZ", "Manzini", null, null, "region-1", null, null));

        var result = await new CaseOccurrenceSearch(queue, locations).SearchAsync(Actor(), "SZ", "region-1", 1, default);
        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal(second.CaseId, Assert.Single(result.Items).Case.CaseId);
        Assert.Equal(2, result.Items[0].ReportRevision);
        Assert.True(result.Truncated);
        Assert.True(result.UnknownLocations);
        Assert.Equal(new string?[] { null, "next" }, queue.Cursors);
    }

    [Fact]
    public async Task UnavailableLocationCannotBecomeAnEmptySuccessfulSearch()
    {
        var row = Row(1);
        var locations = new Locations();
        locations.Results[row.CaseId] = new(CaseOccurrenceLocationAccess.Unavailable, null, null);
        var result = await new CaseOccurrenceSearch(new Queue(new CaseQueueResult(CaseQueueAccess.Allowed, [row])), locations)
            .SearchAsync(Actor(), "SZ", "region-1", 5, default);
        Assert.Equal(CaseQueueAccess.Unavailable, result.Access);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostCaseAccessDuringLocationReadMarksResultsIncomplete(bool includeMatch)
    {
        var lost = Row(1);
        var match = Row(2);
        var locations = new Locations();
        locations.Results[lost.CaseId] = new(CaseOccurrenceLocationAccess.NotFoundOrNotAuthorized, null, null);
        locations.Results[match.CaseId] = new(CaseOccurrenceLocationAccess.Allowed, 1,
            new("SZ", "Manzini", "Area", "Locality", "region-1", "area-1", "locality-1"));
        var rows = includeMatch ? new[] { lost, match } : new[] { lost };

        var result = await new CaseOccurrenceSearch(new Queue(new CaseQueueResult(CaseQueueAccess.Allowed, rows)), locations)
            .SearchAsync(Actor(), "SZ", "region-1", 5, default);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.True(result.Truncated);
        Assert.False(result.UnknownLocations);
        Assert.Equal(includeMatch ? new[] { match.CaseId } : Array.Empty<Guid>(), result.Items.Select(item => item.Case.CaseId));
    }

    [Fact]
    public async Task LaterLocationFailureReturnsOnlyVerifiedMatchesAndMarksTheScanIncomplete()
    {
        var first = Row(1); var second = Row(2); var third = Row(3);
        var locations = new Locations();
        locations.Results[first.CaseId] = new(CaseOccurrenceLocationAccess.Allowed, 1,
            new("SZ", "Manzini", "Area", "Locality", "region-1", "area-1", "locality-1"));
        locations.Results[second.CaseId] = new(CaseOccurrenceLocationAccess.Unavailable, null, null);
        var result = await new CaseOccurrenceSearch(new Queue(new CaseQueueResult(CaseQueueAccess.Allowed, [first, second, third])), locations)
            .SearchAsync(Actor(), "SZ", "region-1", 5, default);
        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal(first.CaseId, Assert.Single(result.Items).Case.CaseId);
        Assert.True(result.Truncated);
        Assert.True(result.LocationReadUnavailable);
    }

    [Fact]
    public async Task RepeatedPageCursorCannotProduceAnApparentlyCompleteResult()
    {
        var first = Row(1); var second = Row(2);
        var locations = new Locations();
        locations.Results[first.CaseId] = new(CaseOccurrenceLocationAccess.Allowed, 1, null);
        locations.Results[second.CaseId] = new(CaseOccurrenceLocationAccess.Allowed, 1, null);
        var queue = new Queue(new(CaseQueueAccess.Allowed, [first], "same"), new(CaseQueueAccess.Allowed, [second], "same"));
        var result = await new CaseOccurrenceSearch(queue, locations).SearchAsync(Actor(), "SZ", "region-1", 5, default);
        Assert.Equal(CaseQueueAccess.Unavailable, result.Access);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task FindsLatestMatchingPlaceBeyondFormerScanLimit()
    {
        var pages = new List<CaseQueueResult>();
        var locations = new Locations();
        for (var page = 0; page < 4; page++)
        {
            var rows = Enumerable.Range(page * 50 + 1, 50).Select(Row).ToArray();
            foreach (var row in rows) locations.Results[row.CaseId] = new(CaseOccurrenceLocationAccess.Allowed, 1,
                new("SZ", "Other", "Area", "Locality", "other", "area", "locality"));
            pages.Add(new(CaseQueueAccess.Allowed, rows, $"page-{page}"));
        }
        var latest = Row(201);
        locations.Results[latest.CaseId] = new(CaseOccurrenceLocationAccess.Allowed, 1,
            new("SZ", "Manzini", "Area", "Locality", "region-1", "area-1", "locality-1"));
        pages.Add(new(CaseQueueAccess.Allowed, [latest]));
        var result = await new CaseOccurrenceSearch(new Queue(pages.ToArray()), locations).SearchAsync(Actor(), "SZ", "region-1", 1, default);
        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.False(result.Truncated);
        Assert.False(result.UnknownLocations);
        Assert.Equal(latest.CaseId, Assert.Single(result.Items).Case.CaseId);
    }
}
