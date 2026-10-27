using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Infrastructure.Persistence.Notifications;

/// <summary>Device token ciphertext and keyed-free high-entropy token fingerprint only.</summary>
public sealed class PushDeviceEntity
{
    public Guid DeviceId { get; set; }
    public required string ActorId { get; set; }
    public Guid InstallationId { get; set; }
    public PushDevicePlatform Platform { get; set; }
    public bool SoundEnabled { get; set; }
    public required byte[] ProtectedToken { get; set; }
    public required byte[] TokenFingerprint { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
