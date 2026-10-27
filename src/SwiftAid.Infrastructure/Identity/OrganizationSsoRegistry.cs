using System.Collections.ObjectModel;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Infrastructure.Identity;

/// <summary>Fixed, server-configured OIDC connections; request data never supplies an authority.</summary>
public sealed class OrganizationSsoRegistry
{
    private readonly IReadOnlyDictionary<string, OrganizationSsoProviderOptions> _providers;
    private readonly bool _federationEnabled;
    private readonly bool _googleEnabled;
    private readonly bool _facebookEnabled;

    public OrganizationSsoRegistry(FederationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _federationEnabled = options.Enabled;
        _googleEnabled = options.Google.Enabled && options.Google.CallbackVerified;
        _facebookEnabled = options.Facebook.Enabled && options.Facebook.CallbackVerified;
        var providers = options.Enabled
            ? options.OrganizationProviders.Where(provider => provider.CallbackVerified).ToDictionary(
                provider => provider.ProviderId,
                provider => provider,
                StringComparer.Ordinal)
            : new Dictionary<string, OrganizationSsoProviderOptions>(StringComparer.Ordinal);
        _providers = new ReadOnlyDictionary<string, OrganizationSsoProviderOptions>(providers);
    }

    public IReadOnlyCollection<string> ProviderIds => _providers.Keys.ToArray();

    public bool TryGet(string providerId, out OrganizationSsoProviderOptions? provider) =>
        _providers.TryGetValue(providerId, out provider);

    /// <summary>Returns whether the exact configured provider and issuer currently permit sign-in.</summary>
    public bool IsUsableLoginRoute(string providerId, string issuer)
    {
        if (!_federationEnabled || string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(issuer))
        {
            return false;
        }

        if (string.Equals(providerId, FederationSetup.GoogleProviderId, StringComparison.Ordinal))
        {
            return _googleEnabled && string.Equals(issuer, FederationSetup.GoogleIssuer, StringComparison.Ordinal);
        }

        if (string.Equals(providerId, FederationSetup.FacebookProviderId, StringComparison.Ordinal))
        {
            return _facebookEnabled && string.Equals(issuer, FederationSetup.FacebookIssuer, StringComparison.Ordinal);
        }

        return TryGet(providerId, out var provider) &&
            provider is not null &&
            string.Equals(provider.Issuer, issuer, StringComparison.Ordinal);
    }
}
