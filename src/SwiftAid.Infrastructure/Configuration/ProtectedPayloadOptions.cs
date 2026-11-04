namespace SwiftAid.Infrastructure.Configuration;

/// <summary>Controls the persistent Data Protection key ring for case-event payloads.</summary>
public sealed class ProtectedPayloadOptions
{
    public const string SectionName = "DataProtection";

    public bool Enabled { get; set; }

    public string ApplicationName { get; set; } = string.Empty;

    public string KeysPath { get; set; } = string.Empty;

    public string CertificatePath { get; set; } = string.Empty;

    public string CertificatePassword { get; set; } = string.Empty;

    public bool AllowUnencryptedDevelopmentKeys { get; set; }

    public long MaximumPayloadBytes { get; set; }
}
