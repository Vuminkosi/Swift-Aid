using SwiftAid.Client.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class DashboardOverviewContractTests
{
    [Theory]
    [InlineData(DashboardOverviewPeriod.Days30, DashboardOverviewSector.Human, "ZW", true)]
    [InlineData(DashboardOverviewPeriod.Days90, DashboardOverviewSector.OneHealth, null, true)]
    [InlineData((DashboardOverviewPeriod)45, DashboardOverviewSector.Human, "ZW", false)]
    [InlineData(DashboardOverviewPeriod.Days30, (DashboardOverviewSector)99, "ZW", false)]
    [InlineData(DashboardOverviewPeriod.Days30, DashboardOverviewSector.Human, "Z W", false)]
    [InlineData(DashboardOverviewPeriod.Days30, DashboardOverviewSector.Human, "Z", false)]
    public void DashboardOverviewQuery_ValidatesBoundedPeriodSectorAndRegion(
        DashboardOverviewPeriod period,
        DashboardOverviewSector sector,
        string? regionCode,
        bool expectedValid)
    {
        var query = new DashboardOverviewQuery(period, regionCode, sector);

        Assert.Equal(expectedValid, query.IsValid);
    }

    [Theory]
    [InlineData("Cholera", "Cholera")]
    [InlineData("Cholera", "CHOLERA")]
    public void DashboardOverview_RejectsDuplicateCategoryKeysIgnoringCase(
        string firstCategoryLabel,
        string secondCategoryLabel)
    {
        var overview = CreateOverview() with
        {
            Categories =
            [
                HumanCategory(firstCategoryLabel, "Reported"),
                HumanCategory(secondCategoryLabel, "Reported")
            ]
        };

        Assert.False(overview.IsValid);
    }

    [Fact]
    public void DashboardOverview_AllowsMatchingCategoryKeysAcrossHumanAndAnimalSectors()
    {
        var overview = CreateOverview() with
        {
            Sector = DashboardOverviewSector.OneHealth,
            Categories =
            [
                HumanCategory("Cholera", "Reported"),
                AnimalCategory("Cholera", "Reported")
            ]
        };

        Assert.True(overview.IsValid);
    }

    [Fact]
    public void DashboardOverview_AllowsSameSectorCategoryWithDifferentClassifications()
    {
        var overview = CreateOverview() with
        {
            Categories =
            [
                HumanCategory("Cholera", "Reported"),
                HumanCategory("Cholera", "Suspected")
            ]
        };

        Assert.True(overview.IsValid);
    }

    [Fact]
    public async Task UnavailableClient_FailsClosedAndPreservesInvalidRequestState()
    {
        var client = new UnavailableDashboardOverviewClient();

        var unavailable = await client.GetAsync(new DashboardOverviewQuery());
        var invalid = await client.GetAsync(new DashboardOverviewQuery(
            Period: (DashboardOverviewPeriod)123));

        Assert.Equal(UiCapabilityResultCode.Unavailable, unavailable.Code);
        Assert.Null(unavailable.Value);
        Assert.False(unavailable.Metadata.IsAvailable);
        Assert.Equal(UiCapabilityProvenance.LiveService, unavailable.Metadata.Provenance);

        Assert.Equal(UiCapabilityResultCode.InvalidRequest, invalid.Code);
        Assert.Null(invalid.Value);
        Assert.False(invalid.Metadata.IsAvailable);
    }

    [Fact]
    public async Task DashboardClients_ObserveCancellationBeforeReturningResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var query = new DashboardOverviewQuery();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new UnavailableDashboardOverviewClient().GetAsync(query, cancellation.Token));
    }

    private static DashboardOverview CreateOverview()
    {
        var asOf = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
        return new DashboardOverview(
            AuthorizedScopeLabel: "Authorized regional scope",
            Period: DashboardOverviewPeriod.Days30,
            Sector: DashboardOverviewSector.Human,
            AsOfUtc: asOf,
            Completeness: DashboardDataCompleteness.Complete,
            FreshnessState: DashboardFreshnessState.Current,
            FreshnessExpiresAtUtc: asOf.AddMinutes(5),
            Kpis:
            [
                new(DashboardKpiKind.ActiveCases, DashboardValueState.Available, 1, "Open cases."),
                new(DashboardKpiKind.AdministrativeHighPriorityCases, DashboardValueState.Available, 0, "Open cases marked for administrative priority handling."),
                new(DashboardKpiKind.NewOperationalAlerts, DashboardValueState.Available, 0, "Administrative events in the reporting window."),
                new(DashboardKpiKind.ClosedInWindow, DashboardValueState.Available, 0, "Cases administratively closed in the reporting window.")
            ],
            Categories: [],
            RecentCases: [],
            RecentOperationalAlerts: []);
    }

    private static DashboardCategoryAggregate HumanCategory(string label, string classification) => new(
        Sector: DashboardOverviewSector.Human,
        CategoryLabel: label,
        ClassificationLabel: classification,
        HumanCaseCount: 1,
        AnimalReportCount: null,
        AnimalsAffected: null,
        Denominator: 1,
        IsSuppressed: false,
        Reason: null);

    private static DashboardCategoryAggregate AnimalCategory(string label, string classification) => new(
        Sector: DashboardOverviewSector.Animal,
        CategoryLabel: label,
        ClassificationLabel: classification,
        HumanCaseCount: null,
        AnimalReportCount: 1,
        AnimalsAffected: 2,
        Denominator: 2,
        IsSuppressed: false,
        Reason: null);
}
