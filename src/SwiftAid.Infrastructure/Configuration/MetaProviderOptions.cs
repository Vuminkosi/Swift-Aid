namespace SwiftAid.Infrastructure.Configuration;

/// <summary>
/// Server-only configuration metadata for a future Meta provider integration. This slice does not
/// probe or call a Meta endpoint, and an enabled setting does not prove provider capability.
/// </summary>
public sealed class MetaProviderOptions
{
    public const string SectionName = "Meta";

    public bool Enabled { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string RouteAlias { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
}
