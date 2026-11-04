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

    [Fact]
    public void AgentFilterUsesActivityDateAndShowsOnlyMatchingRoles()
    {
        var sourceDay = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Local);
        var activityDay = sourceDay.AddDays(2);
        var entry = Entry("source", sourceDay) with
        {
            AgentActivities = [
                new CaseAgentActivity(Guid.NewGuid(), "ops.quality", "quality", "1", new DateTimeOffset(activityDay).ToUniversalTime()),
                new CaseAgentActivity(Guid.NewGuid(), "ops.followup", "follow-up", "1", new DateTimeOffset(activityDay).ToUniversalTime()),
                new CaseAgentActivity(Guid.NewGuid(), "unknown", "other", "1", new DateTimeOffset(activityDay).ToUniversalTime())]
        };
        var followUp = Assert.Single(CaseTimelineEntryFilter.Apply([entry], CaseTimelineEntryFilter.EllaiFollowUp, activityDay, activityDay));
        Assert.Equal("ops.followup", Assert.Single(followUp.AgentActivities).RoleId);
        var enrichment = Assert.Single(CaseTimelineEntryFilter.Apply([entry], CaseTimelineEntryFilter.EllaiEnrichment, activityDay, activityDay));
        Assert.Equal("ops.quality", Assert.Single(enrichment.AgentActivities).RoleId);
        Assert.Equal(3, Assert.Single(CaseTimelineEntryFilter.Apply([entry], CaseTimelineEntryFilter.EllaiActivity, activityDay, activityDay)).AgentActivities.Count);
        Assert.Empty(CaseTimelineEntryFilter.Apply([entry], CaseTimelineEntryFilter.EllaiFollowUp, sourceDay, sourceDay));
        Assert.Equal(3, entry.AgentActivities.Count);
    }

    [Fact]
    public void StaffFilterHidesAgentActivitiesAndEscalationFilterDoesNotIncludeOtherEvents()
    {
        var date = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Local);
        var escalation = Entry("escalation", date) with { EventType = "CaseEscalationRaised", AgentActivities = [
            new CaseAgentActivity(Guid.NewGuid(), "ops.followup", "follow-up", "1", new DateTimeOffset(date).ToUniversalTime())] };
        var other = Entry("other", date) with { EventType = "CaseObservationRecorded" };
        var derived = Entry("derived", date, CaseTimelineEntryKind.Derived) with { EventType = "CaseEscalationRaised" };
        Assert.Equal("escalation", Assert.Single(CaseTimelineEntryFilter.Apply([escalation, derived], CaseTimelineEntryFilter.Escalations, null, null)).Label);
        var staff = CaseTimelineEntryFilter.Apply([escalation, other], CaseTimelineEntryFilter.StaffActions, null, null);
        Assert.Equal(2, staff.Count);
        Assert.All(staff, entry => Assert.Empty(entry.AgentActivities));
        Assert.Equal("escalation", Assert.Single(CaseTimelineEntryFilter.Apply([escalation, other], CaseTimelineEntryFilter.Escalations, null, null)).Label);
        Assert.Empty(CaseTimelineEntryFilter.Apply([other], CaseTimelineEntryFilter.EllaiEnrichment, null, null));
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
