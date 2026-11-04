using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Tests;

/// <summary>
/// P5.2 monitoring next actions: follow-up, escalation, and observation derivation.
/// Pure timeline projection; no clock.
/// </summary>
public sealed class CaseMonitoringNextActionsTests
{
    private static readonly DateTimeOffset AtUtc = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScheduledFollowUpWithoutOutcome_IsOutstanding()
    {
        var timeline = new CaseTimeline(Header(3), [
            Entry(1, "CaseReported", "Report accepted"),
            Entry(2, "CaseFollowUpScheduled", "Follow-up scheduled"),
            Entry(3, "CaseResolved", "Case resolved"),
        ]);

        var actions = CaseNextActions.Derive(timeline);

        Assert.Contains(actions, a => a.Contains("outcome not yet recorded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScheduledFollowUpWithLaterOutcome_IsNotOutstanding()
    {
        var timeline = new CaseTimeline(Header(3), [
            Entry(1, "CaseFollowUpScheduled", "Follow-up scheduled"),
            Entry(2, "CaseFollowUpOutcomeRecorded", "Follow-up outcome recorded"),
            Entry(3, "CaseResolved", "Case resolved"),
        ]);

        var actions = CaseNextActions.Derive(timeline);

        Assert.DoesNotContain(actions, a => a.Contains("outcome not yet recorded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissedOutcome_WordingNeverImpliesCareCompleted()
    {
        var timeline = new CaseTimeline(Header(3), [
            Entry(1, "CaseFollowUpScheduled", "Follow-up scheduled"),
            Entry(2, "CaseFollowUpOutcomeRecorded", "Follow-up outcome recorded"),
            Entry(3, "CaseEscalationRaised", "Escalation raised"),
        ]);

        var actions = CaseNextActions.Derive(timeline);

        foreach (var action in actions)
        {
            Assert.DoesNotContain("completed", action, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("care completed", action, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RaisedEscalationWithoutAcknowledgement_IsOutstanding()
    {
        var timeline = new CaseTimeline(Header(2), [
            Entry(1, "CaseReported", "Report accepted"),
            Entry(2, "CaseEscalationRaised", "Escalation raised"),
        ]);

        var actions = CaseNextActions.Derive(timeline);

        Assert.Contains(actions, a => a.Contains("acknowledgement not yet recorded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RaisedEscalationWithLaterAcknowledgement_IsNotOutstanding()
    {
        var timeline = new CaseTimeline(Header(3), [
            Entry(1, "CaseEscalationRaised", "Escalation raised"),
            Entry(2, "CaseEscalationAcknowledged", "Escalation acknowledged"),
            Entry(3, "CaseResolved", "Case resolved"),
        ]);

        var actions = CaseNextActions.Derive(timeline);

        Assert.DoesNotContain(actions, a => a.Contains("acknowledgement not yet recorded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LatestObservation_IsNotedWithoutSealedContent()
    {
        var timeline = new CaseTimeline(Header(3), [
            Entry(1, "CaseReported", "Report accepted"),
            Entry(2, "CaseObservationRecorded", "Recorded event (CaseObservationRecorded, schema v1)"),
            Entry(3, "CaseResolved", "Case resolved"),
        ]);

        var actions = CaseNextActions.Derive(timeline);

        Assert.Contains(actions, a => a.Contains("Latest observation recorded", StringComparison.Ordinal));
    }

    private static CaseTimelineHeader Header(long version) => new(
        Guid.NewGuid(), "org-zw-1", "workspace-1", "ZW", "Active", "officer-1", version, 1, AtUtc);

    private static CaseTimelineEntry Entry(long version, string type, string label) => new(
        Guid.NewGuid(), type, label, CaseTimelineEntryKind.Authoritative, version, 1,
        Guid.NewGuid(), "officer-1", null, AtUtc, 1);
}
