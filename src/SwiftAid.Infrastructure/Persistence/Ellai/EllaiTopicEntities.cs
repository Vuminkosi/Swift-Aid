namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>Authoritative row for one accepted session topic. The topic id
/// is a stable hash of session and title: re-tracking updates one row, so
/// history never forks and restarts see the same ledger.</summary>
public sealed class EllaiTopicEntity
{
    public string TopicId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string AliasesJson { get; set; } = "[]";
    public string? EntityRef { get; set; }
    public string? EntityVersion { get; set; }
    public string CoverageState { get; set; } = "Partial";
    public string? CheckpointId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
