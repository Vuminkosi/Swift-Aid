namespace SwiftAid.Infrastructure.Configuration;

/// <summary>
/// Controls the narrow local-development exception for plaintext provider transports.
/// Production and missing-host-environment callers never receive the exception.
/// </summary>
public sealed class TransportSecurityOptions
{
    public const string SectionName = "TransportSecurity";

    public bool AllowPlaintextDevelopment { get; set; }

    public string[] AllowedDevelopmentHosts { get; set; } = ["localhost", "127.0.0.1", "::1"];
}
