using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Tests.Cases;

public sealed class CaseReportingSummaryQueryTests
{
    private static ActorContext Actor() => ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "summary-officer"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));

    [Fact]
    public async Task CountsBeyondBothOldCapsAndUsesRecordedPlacesAndDistinctSymptomsPerCase()
    {
        var data = new Data(650);
        var from = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        var result = await new CaseReportingSummaryQuery(data, data).ReadAsync(Actor(), "SZ", "manzini", from,
            from.AddDays(7), true, default);
        Assert.Equal(325, result.Total);
        Assert.Equal(325, result.Classified);
        Assert.Equal(0, result.Unclassified);
        Assert.Equal(325, Assert.Single(result.Observations).Cases);
        Assert.Equal("Cough", result.Observations[0].Label);
        Assert.Equal(4, data.Pages);
        Assert.Equal(from, data.Filter!.ReportedFromUtc);
        Assert.Equal(from.AddDays(7), data.Filter.ReportedBeforeUtc);
    }

    [Fact]
    public async Task CountsClassificationWithoutReadingPrivateReportEvidence()
    {
        var data = new Data(650);
        var result = await new CaseReportingSummaryQuery(data, data).ReadAsync(Actor(), null, null, null, null, false, default);
        Assert.Equal(650, result.Total);
        Assert.Equal(325, result.Classified);
        Assert.Equal(325, result.Unclassified);
        Assert.Equal(0, data.ReportReads);
    }

    [Fact]
    public async Task MissingRegistryPlaceCanMatchExactReportedLabelsWithoutSubstringExpansion()
    {
        var data = new Data(650);
        var result = await new CaseReportingSummaryQuery(data, data)
            .ReadAsync(Actor(), null, null, null, null, true, default, "siphofaneni");
        Assert.Equal(325, result.Total);
        Assert.Equal(325, Assert.Single(result.Observations).Cases);
        Assert.Equal(4, data.Pages);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NeverPublishesPartialCountsWhenPageOrEvidenceFails(bool failPage, bool staleReport)
    {
        var data = new Data(650) { FailPage = failPage, StaleReport = staleReport };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CaseReportingSummaryQuery(data, data)
            .ReadAsync(Actor(), "SZ", "manzini", null, null, true, default));
    }

    [Fact]
    public async Task DemoCaseShellsAreDisclosedAndDoNotMasqueradeAsRecordedReports()
    {
        var data = new Data(10) { FirstRowHasNoJournal = true };
        var result = await new CaseReportingSummaryQuery(data, data)
            .ReadAsync(Actor(), null, null, null, null, true, default);
        Assert.Equal(9, result.Total);
        Assert.Equal(1, result.MissingReportJournals);
        Assert.Equal(9, data.ReportReads);
        Assert.Equal("Human", Assert.Single(result.Categories).Label);
    }

    private sealed class Data(int count) : ICaseQueueQuery, ICaseReportReadQuery
    {
        public int Pages { get; private set; }
        public int ReportReads { get; private set; }
        public bool FailPage { get; init; }
        public bool StaleReport { get; init; }
        public bool FirstRowHasNoJournal { get; init; }
        public CaseQueueFilter? Filter { get; private set; }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<CaseQueueResult> GetSearchableAsync(ActorContext actor, int? limit, CaseQueueFilter? filter, string? cursor, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Filter = filter;
            Pages++;
            if (FailPage && cursor is not null) return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Unavailable, []));
            var start = cursor is null ? 0 : int.Parse(cursor);
            var end = Math.Min(count, start + limit!.Value);
            var rows = Enumerable.Range(start, end - start).Select(index => new CaseQueueItem(
                new Guid(index + 1, 0, 0, new byte[8]), "Reported", "SZ", "summary-officer", 2,
                DateTimeOffset.UnixEpoch, "HumanHealth", index % 2 == 0 ? "Confirmed" : "Unclassified",
                HasReportJournal: !FirstRowHasNoJournal || index != 0)).ToArray();
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, rows, end < count ? end.ToString() : null));
        }
        public Task<CaseReportDetails?> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
        {
            ReportReads++;
            var index = BitConverter.ToInt32(caseId.ToByteArray()) - 1;
            return Task.FromResult<CaseReportDetails?>(new(caseId, 1, null, ["Cough", "cough", " Cough "],
                new("SZ", null, null, index % 2 == 0 ? "Siphofaneni" : "New Siphofaneni", index % 2 == 0 ? "manzini" : "hhohho", null, null),
                AggregateVersion: StaleReport ? 3 : 2));
        }
    }
}
