namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

public enum IntakeAttachmentState
{
    Uploaded,
    Attached,
    Rejected,
    Expired
}

/// <summary>Server-issued MinIO object reference bound to its guest session, capability and report.</summary>
public sealed class IntakeAttachmentEntity
{
    public Guid AttachmentId { get; set; }
    public Guid SessionId { get; set; }
    public Guid CapabilityId { get; set; }
    public Guid ClientSubmissionId { get; set; }
    public required string ObjectKey { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public int DurationSeconds { get; set; }
    public IntakeAttachmentState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? AttachedAtUtc { get; set; }
}
