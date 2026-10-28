namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>
/// Auditable history of one community session: one row per executed command,
/// carrying a snapshot of the public fields. Never updated or deleted by the
/// service; retained even after cancel/complete (SA-SES-09, SA-SES-13).
/// </summary>
public sealed class CommunitySessionRevisionEntity
{
    public Guid RevisionId { get; set; }
    public Guid SessionId { get; set; }
    public long Version { get; set; }
    public string ActorId { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string SnapshotJson { get; set; } = "{}";
}
