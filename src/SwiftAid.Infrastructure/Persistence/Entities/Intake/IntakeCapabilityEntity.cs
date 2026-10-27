namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

/// <summary>One renewable opaque capability; its raw bearer value is never persisted.</summary>
public sealed class IntakeCapabilityEntity
{
    public Guid CapabilityId { get; set; }
    public Guid SessionId { get; set; }
    public required byte[] CapabilityDigest { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}
