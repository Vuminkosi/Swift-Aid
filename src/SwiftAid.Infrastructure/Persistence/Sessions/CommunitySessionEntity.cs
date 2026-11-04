namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>Authoritative row for one community session (P10.1).</summary>
public sealed class CommunitySessionEntity
{
    public Guid SessionId { get; set; }
    public string? OrganizationId { get; set; }
    public long Version { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string AudienceDescription { get; set; } = "";
    public string TopicTagsJson { get; set; } = "[]";
    public string CountryCode { get; set; } = "";
    public string RegionId { get; set; } = "";
    public string Sector { get; set; } = "";
    public string Presenter { get; set; } = "";
    public string Language { get; set; } = "";
    public DateTimeOffset StartUtc { get; set; }
    public string TimeZoneId { get; set; } = "";
    public int DurationMinutes { get; set; }
    public int? Capacity { get; set; }
    public string MaterialsJson { get; set; } = "[]";
    public string Visibility { get; set; } = "Public";
    public string ChannelType { get; set; } = "None";
    public string? ChannelLink { get; set; }
    public string ReminderLeadHoursJson { get; set; } = "[]";
    public string Status { get; set; } = "Draft";
    public string CreatedByActorId { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? ReviewedByActorId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public bool SelfReviewAcknowledged { get; set; }
    public DateTimeOffset? ScheduledAtUtc { get; set; }
    public long PublishedVersion { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
