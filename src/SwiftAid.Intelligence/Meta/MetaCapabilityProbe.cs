namespace SwiftAid.Intelligence.Meta;

public enum CapabilityState
{
    NotProvisioned,
    ConfiguredUnverified,
    Verified,
    Unavailable
}

public sealed record CapabilityAssessment(CapabilityState State)
{
    public bool IsUsable => State == CapabilityState.Verified;
}

public sealed record MetaProviderCapabilities(
    CapabilityAssessment Text,
    CapabilityAssessment Tools,
    CapabilityAssessment Asr,
    CapabilityAssessment AudioFormats,
    CapabilityAssessment Languages,
    CapabilityAssessment Pricing,
    CapabilityAssessment AccountBalance)
{
    public bool IsConfigurationOnly => true;
}

public interface IMetaCapabilityProbe
{
    ValueTask<MetaProviderCapabilities> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Checks source-backed configuration only. It performs no DNS resolution, HTTP request, redirect handling,
/// credential access or provider verification.
/// </summary>
public sealed class MetaCapabilityProbe(
    MetaOptions options,
    IMetaEndpointRegistry? endpointRegistry = null) : IMetaCapabilityProbe
{
    public async ValueTask<MetaProviderCapabilities> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MetaOptionsValidator.Validate(options);

        if (string.IsNullOrWhiteSpace(options.RouteAlias) || string.IsNullOrWhiteSpace(options.ModelId))
        {
            return NotProvisioned();
        }

        var registry = endpointRegistry ?? EmptyMetaEndpointRegistry.Instance;
        var registration = await registry.FindAsync(
            options.RouteAlias,
            options.ModelId,
            cancellationToken).ConfigureAwait(false);

        if (registration is null)
        {
            return NotProvisioned();
        }

        ValidateRegistration(registration, options);

        var text = options.CredentialConfigured
            ? new CapabilityAssessment(CapabilityState.ConfiguredUnverified)
            : new CapabilityAssessment(CapabilityState.NotProvisioned);

        var asr = options.CredentialConfigured && !string.IsNullOrWhiteSpace(registration.AsrModelId)
                ? new CapabilityAssessment(CapabilityState.ConfiguredUnverified)
                : new CapabilityAssessment(CapabilityState.NotProvisioned);

        var tools = registration.ToolsListed && text.State == CapabilityState.ConfiguredUnverified
            ? new CapabilityAssessment(CapabilityState.ConfiguredUnverified)
            : new CapabilityAssessment(CapabilityState.NotProvisioned);

        return new MetaProviderCapabilities(
            text,
            tools,
            asr,
            AssessmentFor(registration.AudioFormatsListed),
            AssessmentFor(registration.LanguagesListed),
            new CapabilityAssessment(CapabilityState.NotProvisioned),
            new CapabilityAssessment(CapabilityState.NotProvisioned));
    }

    private static void ValidateRegistration(MetaEndpointRegistration registration, MetaOptions options)
    {
        if (registration.Route != options.Route ||
            !string.Equals(registration.RouteAlias, options.RouteAlias, StringComparison.Ordinal) ||
            !string.Equals(registration.ModelId, options.ModelId, StringComparison.Ordinal))
        {
            throw new MetaConfigurationException("The endpoint registry returned an unrequested route kind, route or model.");
        }

        if (!registration.PublicAuthorityVerified || string.IsNullOrWhiteSpace(registration.EvidenceReference) ||
            !IsSafeEndpoint(registration.Endpoint, registration.ExpectedAuthority))
        {
            throw new MetaConfigurationException("The source-backed Meta endpoint registration is not safe to use.");
        }
    }

    private static bool IsSafeEndpoint(Uri endpoint, string expectedAuthority)
    {
        if (!endpoint.IsAbsoluteUri ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            endpoint.Port != 443 ||
            Uri.CheckHostName(endpoint.Host) != UriHostNameType.Dns ||
            !string.Equals(endpoint.Authority, expectedAuthority, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = endpoint.IdnHost.TrimEnd('.');
        return !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".intranet", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("gateway.docker.internal", StringComparison.OrdinalIgnoreCase);
    }

    private static MetaProviderCapabilities NotProvisioned()
    {
        var unavailable = new CapabilityAssessment(CapabilityState.NotProvisioned);
        return new MetaProviderCapabilities(unavailable, unavailable, unavailable, unavailable, unavailable, unavailable, unavailable);
    }

    private static CapabilityAssessment AssessmentFor(IReadOnlyCollection<string>? values) =>
        values?.Count > 0
            ? new CapabilityAssessment(CapabilityState.ConfiguredUnverified)
            : new CapabilityAssessment(CapabilityState.NotProvisioned);
}
