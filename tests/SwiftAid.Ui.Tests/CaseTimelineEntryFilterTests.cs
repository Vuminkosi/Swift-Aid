using SwiftAid.Contracts.Cases;
using SwiftAid.UI.Components.Cases;

namespace SwiftAid.Ui.Tests;

public sealed class CaseTimelineEntryFilterTests
{
    [Fact]
    public void Apply_IncludesBothLocalDateBoundsAndKeepsChronologicalOrder()
    {
        var day = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Local);
        var entries = new[]
        {
            Entry("before", day.AddDays(-1).AddHours(23)),
            Entry("first", day.AddMinutes(1)),
            Entry("last", day.AddHours(23).AddMinutes(59)),
            Entry("after", day.AddDays(1))
        };

        var result = CaseTimelineEntryFilter.Apply(entries, "*", day, day);

        Assert.Equal(["first", "last"], result.Select(entry => entry.Label));
    }

    [Fact]
    public void Apply_CombinesEventKindAndDateFilters()
    {
        var day = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Local);
        var entries = new[]
        {
            Entry("authoritative", day, CaseTimelineEntryKind.Authoritative),
            Entry("derived", day.AddHours(1), CaseTimelineEntryKind.Derived),
            Entry("outside", day.AddDays(1), CaseTimelineEntryKind.Derived)
        };

        var result = CaseTimelineEntryFilter.Apply(entries, "kind:derived", day, day);

        Assert.Equal(["derived"], result.Select(entry => entry.Label));
    }

    [Fact]
    public void Apply_ReturnsNoEntriesForReversedRange()
    {
        var entry = Entry("today", new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Local));

        var result = CaseTimelineEntryFilter.Apply(
            [entry],
            "*",
            new DateTime(2026, 11, 1),
            new DateTime(2026, 10, 31));

        Assert.Empty(result);
    }

    private static CaseTimelineEntry Entry(
        string label,
        DateTime recordedLocal,
        CaseTimelineEntryKind kind = CaseTimelineEntryKind.Authoritative) =>
        new(
            Guid.NewGuid(),
            label,
            label,
            kind,
            1,
            1,
            Guid.NewGuid(),
            "actor",
            null,
            new DateTimeOffset(recordedLocal).ToUniversalTime(),
            1);
}
