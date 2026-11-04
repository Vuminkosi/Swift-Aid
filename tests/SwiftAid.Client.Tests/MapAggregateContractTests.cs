using SwiftAid.Client.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class MapAggregateContractTests
{
    [Theory]
    [InlineData("SZ", true)]
    [InlineData("ZW-01", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Z", false)]
    [InlineData("Z W", false)]
    [InlineData("SZ!", false)]
    [InlineData("0123456789012345678901234", false)]
    public void MapAggregateQuery_ValidatesBoundedRegionCode(string? regionCode, bool expectedValid)
    {
        var query = new MapAggregateQuery(new PageRequest(1, 100), regionCode);

        Assert.Equal(expectedValid, query.IsValid);
    }

    [Theory]
    [InlineData(MapAggregatePeriod.Days30, true)]
    [InlineData(MapAggregatePeriod.Days90, true)]
    [InlineData((MapAggregatePeriod)45, false)]
    public void MapAggregateQuery_RejectsUndefinedPeriod(MapAggregatePeriod period, bool expectedValid)
    {
        var query = new MapAggregateQuery(new PageRequest(1, 100), null, period);

        Assert.Equal(expectedValid, query.IsValid);
    }

    [Fact]
    public void MapAggregateQuery_RejectsOversizedPages()
    {
        var query = new MapAggregateQuery(new PageRequest(1, 101));

        Assert.False(query.IsValid);
    }

    [Fact]
    public void SuppressedArea_WithCounts_IsInvalid()
    {
        var area = new MapAreaAggregate(
            "SZ",
            "Eswatini",
            MapAggregateAreaPrecision.Country,
            12,
            3,
            15,
            true,
            "Small counts are withheld to protect privacy.",
            new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.False(area.IsValid);
    }

    [Fact]
    public void SuppressedArea_WithoutCountsOrReason_IsInvalid()
    {
        var area = new MapAreaAggregate(
            "SZ",
            "Eswatini",
            MapAggregateAreaPrecision.Country,
            null,
            null,
            null,
            true,
            null,
            new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.False(area.IsValid);
    }

    [Fact]
    public void SuppressedArea_WithoutCountsAndWithReason_IsValid()
    {
        var area = new MapAreaAggregate(
            "SZ",
            "Eswatini",
            MapAggregateAreaPrecision.Country,
            null,
            null,
            null,
            true,
            "Small counts are withheld to protect privacy.",
            new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.True(area.IsValid);
    }

    [Fact]
    public void CountedArea_WithSuppressionReason_IsInvalid()
    {
        var area = ValidArea() with { SuppressionReason = "Unexpected reason." };

        Assert.False(area.IsValid);
    }

    [Fact]
    public void CountedArea_WithDenominatorBelowKnownCounts_IsInvalid()
    {
        var area = ValidArea() with { Denominator = 10 };

        Assert.False(area.IsValid);
    }

    [Fact]
    public void AggregatePage_RejectsDuplicateAreaCodesIgnoringCase()
    {
        var page = new MapAggregatePage(
            new Page<MapAreaAggregate>(
                [ValidArea(), ValidArea() with { AreaCode = "sz" }],
                1,
                100,
                2,
                false),
            new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.False(page.IsValid);
    }

    [Fact]
    public void AggregatePage_AcceptsDistinctCountryAndRegionRows()
    {
        var page = new MapAggregatePage(
            new Page<MapAreaAggregate>(
                [ValidArea(), ValidArea() with { AreaCode = "SZ-HH", Precision = MapAggregateAreaPrecision.Region }],
                1,
                100,
                2,
                false),
            new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.True(page.IsValid);
    }

    private static MapAreaAggregate ValidArea() => new(
        "SZ",
        "Eswatini",
        MapAggregateAreaPrecision.Country,
        12,
        3,
        15,
        false,
        null,
        new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
}
