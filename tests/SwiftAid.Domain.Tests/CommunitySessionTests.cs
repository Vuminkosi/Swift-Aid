using SwiftAid.Domain.Sessions;

namespace SwiftAid.Domain.Tests;

/// <summary>P10.1 community-session lifecycle (SA-SES-01–SA-SES-04, SA-SES-13; AT-29).</summary>
public sealed class CommunitySessionTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StartAt = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Draft_Reviewed_Scheduled_Completed_Flow()
    {
        var session = CreateDraft();

        session.Review("officer-2", acknowledgeSelfReview: false, CreatedAt.AddHours(1));
        Assert.Equal(CommunitySessionStatus.Reviewed, session.Status);

        session.Schedule("officer-2", CreatedAt.AddHours(2));
        Assert.Equal(CommunitySessionStatus.Scheduled, session.Status);
        Assert.Equal(0, session.IcsSequence);

        session.Complete("officer-2", StartAt.AddHours(1));
        Assert.Equal(CommunitySessionStatus.Completed, session.Status);
    }

    [Fact]
    public void Creator_PublishingOwnDraft_RequiresExplicitSelfReviewAcknowledgement()
    {
        var session = CreateDraft();

        Assert.Throws<InvalidOperationException>(() =>
            session.Review("officer-1", acknowledgeSelfReview: false, CreatedAt.AddHours(1)));

        session.Review("officer-1", acknowledgeSelfReview: true, CreatedAt.AddHours(1));
        Assert.Equal(CommunitySessionStatus.Reviewed, session.Status);
        Assert.True(session.SelfReviewAcknowledged);
    }

    [Fact]
    public void Schedule_RequiresReviewedState_AndFutureStart()
    {
        var draft = CreateDraft();
        Assert.Throws<InvalidOperationException>(() => draft.Schedule("officer-2", CreatedAt.AddHours(1)));

        var session = CreateDraft();
        session.Review("officer-2", false, CreatedAt.AddHours(1));
        Assert.Throws<InvalidOperationException>(() => session.Schedule("officer-2", StartAt.AddHours(1)));
    }

    [Fact]
    public void Cancel_RequiresReason_AndSuppressingState()
    {
        var session = CreateDraft();
        Assert.Throws<InvalidOperationException>(() => session.Cancel("reason", "officer-1", CreatedAt.AddHours(1)));

        session.Review("officer-2", false, CreatedAt.AddHours(1));
        Assert.Throws<ArgumentException>(() => session.Cancel("", "officer-2", CreatedAt.AddHours(2)));

        session.Cancel("venue flooded", "officer-2", CreatedAt.AddHours(2));
        Assert.Equal(CommunitySessionStatus.Cancelled, session.Status);
        Assert.Equal("venue flooded", session.CancellationReason);
    }

    [Fact]
    public void Update_OnScheduledSession_BumpsIcsSequence()
    {
        var session = CreateDraft();
        session.Review("officer-2", false, CreatedAt.AddHours(1));
        session.Schedule("officer-2", CreatedAt.AddHours(2));
        var publishedVersion = session.Version;
        Assert.Equal(0, session.IcsSequence);

        session.UpdateDetails(
            "Title v2", "summary", "audience", [], "presenter", "en",
            StartAt.AddDays(1), 60, null,
            [new SessionMaterialRef("mat-1", "v3")],
            SessionVisibility.Public, SessionChannelType.None, null, [],
            StartAt);

        Assert.Equal(publishedVersion + 1, session.Version);
        Assert.Equal(1, session.IcsSequence);
        Assert.Equal(StartAt.AddDays(1), session.StartUtc);
    }

    [Fact]
    public void Complete_RequiresScheduledState_AndStartReached()
    {
        var session = CreateDraft();
        session.Review("officer-2", false, CreatedAt.AddHours(1));
        session.Schedule("officer-2", CreatedAt.AddHours(2));

        Assert.Throws<InvalidOperationException>(() => session.Complete("officer-2", StartAt.AddHours(-1)));
    }

    [Fact]
    public void UnknownTimeZone_IsRejected_AndHarareMapsToCorrectUtc()
    {
        Assert.Throws<ArgumentException>(() => CreateDraft(timeZoneId: "Not/AZone"));

        var harare = TimeZoneInfo.FindSystemTimeZoneById("Africa/Harare");
        var localStart = new DateTimeOffset(2026, 11, 5, 12, 0, 0, harare.GetUtcOffset(new DateTime(2026, 11, 5)));
        var session = CreateDraft(startUtc: localStart, timeZoneId: "Africa/Harare");
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), session.StartUtc);
    }

    [Fact]
    public void Ics_HasStableUid_IncrementingSequence_AndNoAttendeeData()
    {
        var session = CreateDraft();
        session.Review("officer-2", false, CreatedAt.AddHours(1));
        session.Schedule("officer-2", CreatedAt.AddHours(2));

        var first = CommunitySessionSharing.BuildIcs(session, CreatedAt.AddHours(3));
        Assert.Contains($"UID:swiftaid-community-session-{session.SessionId:D}", first);
        Assert.Contains("SEQUENCE:0", first);
        Assert.Contains("STATUS:CONFIRMED", first);
        Assert.DoesNotContain("attendee", first, StringComparison.OrdinalIgnoreCase);

        session.UpdateDetails(
            session.Title, session.Summary, session.AudienceDescription,
            [.. session.TopicTags], session.Presenter, session.Language,
            session.StartUtc, session.DurationMinutes, session.Capacity,
            [.. session.Materials.Select(m => new SessionMaterialRef(m.MaterialId, m.SourceVersion))],
            SessionVisibility.Public, session.ChannelType, session.ChannelLink,
            [.. session.ReminderLeadHours], CreatedAt);
        var second = CommunitySessionSharing.BuildIcs(session, CreatedAt.AddHours(4));
        Assert.Contains($"UID:swiftaid-community-session-{session.SessionId:D}", second);
        Assert.Contains("SEQUENCE:1", second);
    }

    [Fact]
    public void Ics_CancelledSession_ReportsCancellation()
    {
        var session = CreateDraft();
        session.Review("officer-2", false, CreatedAt.AddHours(1));
        session.Schedule("officer-2", CreatedAt.AddHours(2));
        session.Cancel("venue flooded", "officer-2", CreatedAt.AddHours(3));

        var ics = CommunitySessionSharing.BuildIcs(session, CreatedAt.AddHours(4));
        Assert.Contains("STATUS:CANCELLED", ics);
        Assert.Contains("cancelled", ics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ics_DraftSession_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => CommunitySessionSharing.BuildIcs(CreateDraft(), CreatedAt));
    }

    [Fact]
    public void InvitationText_CarriesLogisticsOnly()
    {
        var session = CreateDraft();
        session.Review("officer-2", false, CreatedAt.AddHours(1));
        session.Schedule("officer-2", CreatedAt.AddHours(2));

        var text = CommunitySessionSharing.BuildInvitationText(session);
        Assert.Contains(session.Title, text);
        Assert.Contains("Africa/Harare", text);
        Assert.Contains(session.Presenter, text);
    }

    [Fact]
    public void ReminderOccurrences_OnlyIncludeFutureLeads()
    {
        var session = CreateDraft(reminderLeadHours: [72, 24, 1]);
        // Scheduling a day before the start: the 72h lead already passed.
        var scheduledAt = StartAt.AddHours(-25);
        var occurrences = session.ComputeReminderOccurrences(scheduledAt);

        Assert.Equal([24, 1], occurrences.Select(o => o.LeadHours).ToArray());
        Assert.All(occurrences, o => Assert.True(o.RemindAtUtc > scheduledAt));
    }

    private static CommunitySession CreateDraft(
        DateTimeOffset? startUtc = null,
        string timeZoneId = "Africa/Harare",
        IReadOnlyList<int>? reminderLeadHours = null) =>
        CommunitySession.Create(
            Guid.NewGuid(),
            "Dry-season holding hygiene",
            "Practical steps for clean holding areas.",
            "Livestock keepers near Harare showgrounds",
            ["hygiene"],
            "ZW",
            "region-zw-harare",
            "Animal",
            "officer-2",
            "en",
            startUtc ?? StartAt,
            timeZoneId,
            60,
            40,
            [new SessionMaterialRef("mat-hygiene-1", "v2")],
            SessionVisibility.Public,
            SessionChannelType.None,
            null,
            reminderLeadHours ?? [24, 1],
            "officer-1",
            CreatedAt);
}
