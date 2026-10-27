namespace SwiftAid.Infrastructure.Configuration;

/// <summary>Server-only configuration for SwiftAid-issued authorization-code sessions.</summary>
public sealed class IdentityServerOptions
{
    public const string SectionName = "Identity";

    public bool Enabled { get; set; }

    public string Issuer { get; set; } = string.Empty;

    public string SigningCertificatePath { get; set; } = string.Empty;

    public string SigningCertificatePassword { get; set; } = string.Empty;

    public string EncryptionCertificatePath { get; set; } = string.Empty;

    public string EncryptionCertificatePassword { get; set; } = string.Empty;

    public int AuthorizationCodeLifetimeSeconds { get; set; } = 120;

    public int AccessTokenLifetimeSeconds { get; set; } = 600;

    public int RefreshTokenLifetimeSeconds { get; set; } = 2_592_000;

    public List<IdentityPublicClientOptions> PublicClients { get; set; } = [];
}

/// <summary>Operator-allowlisted native client metadata; never supplied by a login request.</summary>
public sealed class IdentityPublicClientOptions
{
    public string ClientId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<string> RedirectUris { get; set; } = [];

    public List<string> PostLogoutRedirectUris { get; set; } = [];
}
