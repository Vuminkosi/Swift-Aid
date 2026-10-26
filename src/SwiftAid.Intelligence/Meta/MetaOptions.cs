namespace SwiftAid.Intelligence.Meta;

public enum InferenceRouteKind
{
    MetaHostedApi,
    MetaSelfHostedOpenWeight,
    MetaPartnerOpenWeight,
    Unsupported
}

public sealed class MetaOptions
{
    public string? Provider { get; init; }

    public InferenceRouteKind Route { get; init; }

    public string? RouteAlias { get; init; }

    public string? ModelId { get; init; }

    public bool CredentialConfigured { get; init; }
}

public sealed class MetaConfigurationException(string message) : InvalidOperationException(message);

public static class MetaOptionsValidator
{
    public static void Validate(MetaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.Equals(options.Provider, "meta", StringComparison.OrdinalIgnoreCase))
        {
            throw new MetaConfigurationException("Only an explicitly selected Meta provider is supported.");
        }

        if (!Enum.IsDefined(options.Route) || options.Route == InferenceRouteKind.Unsupported)
        {
            throw new MetaConfigurationException("The configured inference route is not supported.");
        }

    }
}
