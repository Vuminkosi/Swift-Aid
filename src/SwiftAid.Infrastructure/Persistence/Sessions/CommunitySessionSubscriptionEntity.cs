namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>Private account/session reminder preference; it is never a public attendee record.</summary>
public sealed class CommunitySessionSubscriptionEntity
{
    public Guid SessionId { get; set; }
    public required string AccountId { get; set; }
    public required string Channel { get; set; }
    public required string ConsentReference { get; set; }
    public string Status { get; set; } = "Active";
    public long Version { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
