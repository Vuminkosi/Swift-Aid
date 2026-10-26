namespace SwiftAid.Infrastructure.Configuration;

public sealed class FederationOptions
{
    public const string SectionName = "Federation";

    public bool Enabled { get; set; }

    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;
}
