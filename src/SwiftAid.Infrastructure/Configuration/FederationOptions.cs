namespace SwiftAid.Infrastructure.Configuration;

public sealed class FederationOptions
{
    public const string SectionName = "Federation";

    public bool Enabled { get; set; }

    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public SocialProviderOptions Google { get; set; } = new();

    public SocialProviderOptions Facebook { get; set; } = new();

    public FacebookWebLoginSettings FacebookWebLogin { get; set; } = new();

    public List<OrganizationSsoProviderOptions> OrganizationProviders { get; set; } = [];
}

/// <summary>Separate readiness and resource limits for the Facebook browser SDK flow.</summary>
public sealed class FacebookWebLoginSettings
{
    public bool Enabled { get; set; }

    public string RateLimitHmacKeyBase64 { get; set; } = string.Empty;

    public string GraphApiVersion { get; set; } = string.Empty;

    public int MaximumRequestBytes { get; set; } = 4096;

    public int MaximumResponseBytes { get; set; } = 16384;

    public int OutboundTimeoutSeconds { get; set; } = 5;

    public int MaximumConcurrentValidations { get; set; } = 10;

    public int MaximumAttempts { get; set; } = 10;

    public int RateLimitWindowSeconds { get; set; } = 60;
}

public sealed class SocialProviderOptions
{
    public bool Enabled { get; set; }

    /// <summary>Local callback path used by the Google handler after configuration validation.</summary>
    public string CallbackPath { get; set; } = "/google/callback";

    /// <summary>Set only after real provider registration and callback checks have passed.</summary>
    public bool CallbackVerified { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;
}

public sealed class OrganizationSsoProviderOptions
{
    public string ProviderId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Set only after real tenant registration and callback checks have passed.</summary>
    public bool CallbackVerified { get; set; }

    public string Authority { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string TenantClaimType { get; set; } = string.Empty;

    public string TenantClaimValue { get; set; } = string.Empty;
}
