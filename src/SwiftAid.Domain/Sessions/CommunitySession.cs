namespace SwiftAid.Domain.Sessions;

/// <summary>Publication lifecycle of an officer-scheduled community education session.</summary>
public enum CommunitySessionStatus
{
    Draft,
    Reviewed,
    Scheduled,
    Completed,
    Cancelled
}

/// <summary>How the public joins: a manually pasted destination link only. SwiftAid never posts or broadcasts.</summary>
public enum SessionChannelType
{
    None,
    FacebookPage,
    WhatsAppGroup,
    Other
}

/// <summary>Whether the session may appear in public listings and calendars.</summary>
public enum SessionVisibility
{
    Public,
    Internal
}

/// <summary>Opaque reference to a human-authored source card and the exact version bound at review time.</summary>
public sealed record SessionMaterialRef(string MaterialId, string SourceVersion);

/// <summary>One computed reminder occurrence. Dispatch itself belongs to the durable reminder lane.</summary>
public sealed record SessionReminderOccurrence(DateTimeOffset RemindAtUtc, int LeadHours);

/// <summary>
/// Officer-scheduled community education session. Native scheduling only: no social-platform
/// API is used or implied. Review is an auditable action; a creator may only publish their own
/// draft with an explicit self-review acknowledgement (PC-08).
/// </summary>
public sealed class CommunitySession
{
    public Guid SessionId { get; }
    public long Version { get; private set; }
    public string Title { get; private set; }
    public string Summary { get; private set; }
    public string AudienceDescription { get; private set; }
    public IReadOnlyList<string> TopicTags { get; private set; }
    public string CountryCode { get; private set; }
    public string RegionId { get; private set; }
    public string Sector { get; private set; }
    public string Presenter { get; private set; }
    public string Language { get; private set; }
    public DateTimeOffset StartUtc { get; private set; }
    public string TimeZoneId { get; private set; }
    public int DurationMinutes { get; private set; }
    public int? Capacity { get; private set; }
    public IReadOnlyList<SessionMaterialRef> Materials { get; private set; }
    public SessionVisibility Visibility { get; private set; }
    public SessionChannelType ChannelType { get; private set; }
    public string? ChannelLink { get; private set; }
    /// <summary>Safe destination for public projections, including records created before current validation.</summary>
    public string? PublicChannelLink => ChannelType != SessionChannelType.None && IsSafeChannelLink(ChannelLink)
        ? ChannelLink : null;
    public IReadOnlyList<int> ReminderLeadHours { get; private set; }
    public CommunitySessionStatus Status { get; private set; }
    public string CreatedByActorId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string? ReviewedByActorId { get; private set; }
    public DateTimeOffset? ReviewedAtUtc { get; private set; }
    public bool SelfReviewAcknowledged { get; private set; }
    public DateTimeOffset? ScheduledAtUtc { get; private set; }
    public long PublishedVersion { get; private set; }
    public string? CancellationReason { get; private set; }
    public DateTimeOffset? CancelledAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>ICS SEQUENCE: 0 at first publication, increasing with every published revision.</summary>
    public long IcsSequence => Status is CommunitySessionStatus.Scheduled or CommunitySessionStatus.Cancelled or CommunitySessionStatus.Completed
        ? Version - PublishedVersion
        : 0;

    private CommunitySession(
        Guid sessionId,
        string title,
        string summary,
        string audienceDescription,
        IReadOnlyList<string> topicTags,
        string countryCode,
        string regionId,
        string sector,
        string presenter,
        string language,
        DateTimeOffset startUtc,
        string timeZoneId,
        int durationMinutes,
        int? capacity,
        IReadOnlyList<SessionMaterialRef> materials,
        SessionVisibility visibility,
        SessionChannelType channelType,
        string? channelLink,
        IReadOnlyList<int> reminderLeadHours,
        string createdByActorId,
        DateTimeOffset createdAtUtc)
    {
        SessionId = sessionId;
        Version = 1;
        Title = title;
        Summary = summary;
        AudienceDescription = audienceDescription;
        TopicTags = topicTags;
        CountryCode = countryCode;
        RegionId = regionId;
        Sector = sector;
        Presenter = presenter;
        Language = language;
        StartUtc = startUtc;
        TimeZoneId = timeZoneId;
        DurationMinutes = durationMinutes;
        Capacity = capacity;
        Materials = materials;
        Visibility = visibility;
        ChannelType = channelType;
        ChannelLink = channelLink;
        ReminderLeadHours = reminderLeadHours;
        Status = CommunitySessionStatus.Draft;
        CreatedByActorId = createdByActorId;
        CreatedAtUtc = createdAtUtc;
    }

    public static CommunitySession Create(
        Guid sessionId,
        string title,
        string summary,
        string audienceDescription,
        IReadOnlyList<string> topicTags,
        string countryCode,
        string regionId,
        string sector,
        string presenter,
        string language,
        DateTimeOffset startUtc,
        string timeZoneId,
        int durationMinutes,
        int? capacity,
        IReadOnlyList<SessionMaterialRef> materials,
        SessionVisibility visibility,
        SessionChannelType channelType,
        string? channelLink,
        IReadOnlyList<int> reminderLeadHours,
        string createdByActorId,
        DateTimeOffset createdAtUtc)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        RequireText(title, nameof(title));
        RequireText(summary, nameof(summary));
        RequireText(audienceDescription, nameof(audienceDescription));
        RequireText(countryCode, nameof(countryCode));
        RequireText(regionId, nameof(regionId));
        RequireText(sector, nameof(sector));
        RequireText(presenter, nameof(presenter));
        RequireText(language, nameof(language));
        RequireText(createdByActorId, nameof(createdByActorId));
        if (startUtc == default) throw new ArgumentException("A session start is required.", nameof(startUtc));
        RequireTimeZone(timeZoneId);
        if (durationMinutes < 1 || durationMinutes > 1440) throw new ArgumentOutOfRangeException(nameof(durationMinutes), "Duration must be between 1 and 1440 minutes.");
        if (capacity is not null && capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive when set.");
        var checkedMaterials = CheckMaterials(materials);
        var checkedTags = CheckTags(topicTags);
        var checkedLeads = CheckReminderLeads(reminderLeadHours);
        var checkedLink = CheckChannelLink(channelType, channelLink);
        return new CommunitySession(
            sessionId, title, summary, audienceDescription, checkedTags, countryCode, regionId,
            sector, presenter, language, startUtc.ToUniversalTime(), timeZoneId, durationMinutes,
            capacity, checkedMaterials, visibility, channelType, checkedLink, checkedLeads,
            createdByActorId, createdAtUtc);
    }

    public void Review(string reviewerActorId, bool acknowledgeSelfReview, DateTimeOffset reviewedAtUtc)
    {
        RequireText(reviewerActorId, nameof(reviewerActorId));
        if (Status != CommunitySessionStatus.Draft) throw new InvalidOperationException("Only a draft can be reviewed.");
        var selfReview = string.Equals(reviewerActorId, CreatedByActorId, StringComparison.Ordinal);
        if (selfReview && !acknowledgeSelfReview)
        {
            throw new InvalidOperationException("A creator publishing their own draft must explicitly acknowledge the self-review.");
        }

        ReviewedByActorId = reviewerActorId;
        ReviewedAtUtc = reviewedAtUtc;
        SelfReviewAcknowledged = selfReview && acknowledgeSelfReview;
        Status = CommunitySessionStatus.Reviewed;
        Version++;
    }

    public void Schedule(string scheduledByActorId, DateTimeOffset utcNow)
    {
        RequireText(scheduledByActorId, nameof(scheduledByActorId));
        if (Status != CommunitySessionStatus.Reviewed) throw new InvalidOperationException("Only a reviewed session can be scheduled.");
        if (StartUtc <= utcNow) throw new InvalidOperationException("A session can only be scheduled for a future start.");
        ScheduledAtUtc = utcNow;
        Status = CommunitySessionStatus.Scheduled;
        Version++;
        PublishedVersion = Version;
    }

    public void UpdateDetails(
        string title,
        string summary,
        string audienceDescription,
        IReadOnlyList<string> topicTags,
        string presenter,
        string language,
        DateTimeOffset startUtc,
        int durationMinutes,
        int? capacity,
        IReadOnlyList<SessionMaterialRef> materials,
        SessionVisibility visibility,
        SessionChannelType channelType,
        string? channelLink,
        IReadOnlyList<int> reminderLeadHours,
        DateTimeOffset utcNow)
    {
        if (Status is not (CommunitySessionStatus.Draft or CommunitySessionStatus.Reviewed or CommunitySessionStatus.Scheduled))
        {
            throw new InvalidOperationException("Only a draft, reviewed, or scheduled session can be updated.");
        }

        RequireText(title, nameof(title));
        RequireText(summary, nameof(summary));
        RequireText(audienceDescription, nameof(audienceDescription));
        RequireText(presenter, nameof(presenter));
        RequireText(language, nameof(language));
        if (startUtc == default) throw new ArgumentException("A session start is required.", nameof(startUtc));
        if (Status == CommunitySessionStatus.Scheduled && startUtc <= utcNow)
        {
            throw new InvalidOperationException("A scheduled session can only be moved to a future start.");
        }

        if (durationMinutes < 1 || durationMinutes > 1440) throw new ArgumentOutOfRangeException(nameof(durationMinutes), "Duration must be between 1 and 1440 minutes.");
        if (capacity is not null && capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive when set.");

        var checkedTags = CheckTags(topicTags);
        var checkedMaterials = CheckMaterials(materials);
        var checkedChannelLink = CheckChannelLink(channelType, channelLink);
        var checkedReminderLeadHours = CheckReminderLeads(reminderLeadHours);

        // A time change may be applied to a published session, but the ordinary
        // update command cannot stand in for review of new public content.
        if (Status == CommunitySessionStatus.Scheduled &&
            (title != Title || summary != Summary || audienceDescription != AudienceDescription ||
             !checkedTags.SequenceEqual(TopicTags) || presenter != Presenter || language != Language ||
             durationMinutes != DurationMinutes || capacity != Capacity ||
             !checkedMaterials.SequenceEqual(Materials) || visibility != Visibility ||
             channelType != ChannelType || checkedChannelLink != ChannelLink))
        {
            throw new InvalidOperationException("Changes to published session content require a reviewed revision.");
        }

        Title = title;
        Summary = summary;
        AudienceDescription = audienceDescription;
        TopicTags = checkedTags;
        Presenter = presenter;
        Language = language;
        StartUtc = startUtc.ToUniversalTime();
        DurationMinutes = durationMinutes;
        Capacity = capacity;
        Materials = checkedMaterials;
        Visibility = visibility;
        ChannelType = channelType;
        ChannelLink = checkedChannelLink;
        ReminderLeadHours = checkedReminderLeadHours;
        if (Status == CommunitySessionStatus.Reviewed)
        {
            Status = CommunitySessionStatus.Draft;
            ReviewedByActorId = null;
            ReviewedAtUtc = null;
            SelfReviewAcknowledged = false;
        }
        Version++;
    }

    public void Cancel(string reason, string cancelledByActorId, DateTimeOffset cancelledAtUtc)
    {
        RequireText(reason, nameof(reason));
        RequireText(cancelledByActorId, nameof(cancelledByActorId));
        if (Status is not (CommunitySessionStatus.Reviewed or CommunitySessionStatus.Scheduled))
        {
            throw new InvalidOperationException("Only a reviewed or scheduled session can be cancelled.");
        }

        CancellationReason = reason;
        CancelledAtUtc = cancelledAtUtc;
        Status = CommunitySessionStatus.Cancelled;
        Version++;
    }

    public void Complete(string completedByActorId, DateTimeOffset completedAtUtc)
    {
        RequireText(completedByActorId, nameof(completedByActorId));
        if (Status != CommunitySessionStatus.Scheduled) throw new InvalidOperationException("Only a scheduled session can be completed.");
        if (completedAtUtc < StartUtc) throw new InvalidOperationException("A session cannot complete before it starts.");
        CompletedAtUtc = completedAtUtc;
        Status = CommunitySessionStatus.Completed;
        Version++;
    }

    /// <summary>Computes reminder occurrences from the bound lead hours; only future ones are due.</summary>
    public IReadOnlyList<SessionReminderOccurrence> ComputeReminderOccurrences(DateTimeOffset scheduledAtUtc) =>
        ReminderLeadHours
            .Select(hours => new SessionReminderOccurrence(StartUtc.AddHours(-hours), hours))
            .Where(occurrence => occurrence.RemindAtUtc > scheduledAtUtc)
            .OrderBy(occurrence => occurrence.RemindAtUtc)
            .ToArray();

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", name);
    }

    private static void RequireTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) throw new ArgumentException("A time zone is required.", nameof(timeZoneId));
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new ArgumentException("The time zone is unknown on this host.", nameof(timeZoneId), ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new ArgumentException("The time zone data is invalid on this host.", nameof(timeZoneId), ex);
        }
    }

    private static IReadOnlyList<SessionMaterialRef> CheckMaterials(IReadOnlyList<SessionMaterialRef>? materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        if (materials.Count == 0) throw new ArgumentException("At least one source-linked material is required.", nameof(materials));
        foreach (var material in materials)
        {
            ArgumentNullException.ThrowIfNull(material);
            RequireText(material.MaterialId, "material.MaterialId");
            RequireText(material.SourceVersion, "material.SourceVersion");
        }

        return Array.AsReadOnly(materials.ToArray());
    }

    private static IReadOnlyList<string> CheckTags(IReadOnlyList<string>? tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        foreach (var tag in tags)
        {
            RequireText(tag, "topicTags");
        }

        return Array.AsReadOnly(tags.ToArray());
    }

    private static IReadOnlyList<int> CheckReminderLeads(IReadOnlyList<int>? leads)
    {
        ArgumentNullException.ThrowIfNull(leads);
        if (leads.Distinct().Count() != leads.Count) throw new ArgumentException("Reminder lead hours must be distinct.", nameof(leads));
        foreach (var lead in leads)
        {
            if (lead < 1 || lead > 168) throw new ArgumentOutOfRangeException(nameof(leads), "Reminder lead hours must be between 1 and 168.");
        }

        return Array.AsReadOnly(leads.ToArray());
    }

    private static string? CheckChannelLink(SessionChannelType channelType, string? channelLink)
    {
        if (channelType == SessionChannelType.None)
        {
            if (channelLink is not null) throw new ArgumentException("A channel link requires a channel type.", nameof(channelLink));
            return null;
        }

        RequireText(channelLink!, nameof(channelLink));
        if (!IsSafeChannelLink(channelLink))
        {
            throw new ArgumentException("A channel link must be an absolute http(s) URL without credentials or control characters.", nameof(channelLink));
        }

        return channelLink;
    }

    private static bool IsSafeChannelLink(string? link) => !string.IsNullOrWhiteSpace(link) &&
        !link.Any(char.IsControl) && Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(uri.UserInfo);
}
