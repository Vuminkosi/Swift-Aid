using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;
using SwiftAid.Infrastructure.Identity;

namespace SwiftAid.Infrastructure.Configuration;

/// <summary>Validates enabled provider settings without connecting to any provider.</summary>
public sealed class ProviderOptionsValidator :
    IValidateOptions<MinioOptions>,
    IValidateOptions<RedisOptions>,
    IValidateOptions<NatsOptions>,
    IValidateOptions<JobPublisherOptions>,
    IValidateOptions<FcmOptions>,
    IValidateOptions<QdrantOptions>,
    IValidateOptions<MetaProviderOptions>,
    IValidateOptions<FederationOptions>,
    IValidateOptions<IdentityServerOptions>,
    IValidateOptions<SwiftAidDatabaseOptions>
{
    private readonly IHostEnvironment? _hostEnvironment;
    private readonly TransportSecurityOptions _transportSecurityOptions;

    public ProviderOptionsValidator(
        IHostEnvironment? hostEnvironment = null,
        TransportSecurityOptions? transportSecurityOptions = null)
    {
        _hostEnvironment = hostEnvironment;
        _transportSecurityOptions = transportSecurityOptions ?? new TransportSecurityOptions();
    }

    public ValidateOptionsResult Validate(string? name, MinioOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (!TryGetMinioEndpoint(options.Endpoint, options.UseSsl, out var endpoint) ||
            (!options.UseSsl && !IsPlaintextAllowed(endpoint.Host)))
        {
            return Invalid("Minio:Endpoint must be an absolute HTTP(S) endpoint without credentials.");
        }

        if (!IsValidBucket(options.Bucket))
        {
            return Invalid("Minio:Bucket must be a valid private bucket name.");
        }

        if (options.MaxObjectSizeBytes <= 0)
        {
            return Invalid("Minio:MaxObjectSizeBytes must be positive when storage is enabled.");
        }

        return HasValue(options.AccessKey) && HasValue(options.SecretKey)
            ? ValidateOptionsResult.Success
            : Invalid("Minio credentials are required when storage is enabled.");
    }

    public ValidateOptionsResult Validate(string? name, RedisOptions options)
    {
        if (options.MaximumTtlSeconds is < 1 or > RedisOptions.MaximumSupportedTtlSeconds)
        {
            return Invalid($"Redis:MaximumTtlSeconds must be between 1 and {RedisOptions.MaximumSupportedTtlSeconds}.");
        }

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (!HasValue(options.ConnectionString) ||
            !HasValue(options.InstanceName) ||
            !options.InstanceName.EndsWith(':'))
        {
            return Invalid("Redis requires a connection string and key prefix when enabled.");
        }

        try
        {
            var redisConfiguration = ConfigurationOptions.Parse(options.ConnectionString);
            if (redisConfiguration.EndPoints.Count == 0)
            {
                return Invalid("Redis:ConnectionString must include at least one endpoint.");
            }

            return redisConfiguration.Ssl || ArePlaintextRedisEndpointsAllowed(redisConfiguration)
                ? ValidateOptionsResult.Success
                : Invalid("Redis:ConnectionString must enable TLS unless an allowlisted Development plaintext exception applies.");
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return Invalid("Redis:ConnectionString is invalid.");
        }
    }

    public ValidateOptionsResult Validate(string? name, NatsOptions options)
    {
        if (options.NotificationStreamMaxAgeSeconds is < 1 or > 31_536_000 ||
            options.NotificationStreamMaxBytes is < 1_048_576 or > 1_073_741_824 ||
            options.NotificationStreamMaxMessages is < 1 or > 10_000_000 ||
            options.NotificationStreamMaxMessageSizeBytes is < 1_024 or > 1_048_576 ||
            options.NotificationStreamReplicas is < 1 or > 5 ||
            options.NotificationStreamDuplicateWindowSeconds is < 1 or > 86_400 ||
            options.NotificationConsumerAckWaitSeconds is < 5 or > 300 ||
            options.NotificationConsumerMaxDeliveries is < 1 or > 8 ||
            options.NotificationConsumerInactiveThresholdSeconds is < 60 or > 3600)
        {
            return Invalid("Nats notification stream retention, size, replication, duplicate window, acknowledgement, delivery, or cleanup settings are outside their supported bounds.");
        }

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (!TryGetNatsEndpoint(options.Url, out var endpoint) ||
            !HasValue(options.Username) ||
            !HasValue(options.Password) ||
            !IsStreamName(options.EventStream) ||
            !IsStreamName(options.NotificationStream) ||
            !IsSubjectPrefix(options.EventSubjectPrefix) ||
            !IsSubject(options.NotificationSubject))
        {
            return Invalid("NATS requires a valid endpoint, credentials, stream names, and literal subjects.");
        }

        if (!string.Equals(options.NotificationStream, "SWIFTAID_NOTIFICATIONS", StringComparison.Ordinal) ||
            !string.Equals(options.NotificationSubject, "swiftaid.notifications.inapp", StringComparison.Ordinal))
        {
            return Invalid("NATS notification stream and subject must use the approved server-owned names.");
        }

        if (endpoint.Scheme == "nats" && !IsPlaintextAllowed(endpoint.Host))
        {
            return Invalid("Nats:Url must use TLS unless an allowlisted Development plaintext exception applies.");
        }

        return ValidateOptionsResult.Success;
    }

    public ValidateOptionsResult Validate(string? name, JobPublisherOptions options)
    {
        if (!string.Equals(
                options.StreamName,
                JobPublisherOptions.ExpectedStreamName,
                StringComparison.Ordinal))
        {
            return Invalid("Nats:Jobs:StreamName must be SWIFTAID_JOBS.");
        }

        return options.MaximumMessageBytes is >= 1 and <= SwiftAid.Contracts.Jobs.JobEnvelope.MaximumMessageBytes
            ? ValidateOptionsResult.Success
            : Invalid($"Nats:Jobs:MaximumMessageBytes must be between 1 and {SwiftAid.Contracts.Jobs.JobEnvelope.MaximumMessageBytes}.");
    }

    public ValidateOptionsResult Validate(string? name, FcmOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        return HasValue(options.ProjectId) &&
            Path.IsPathFullyQualified(options.CredentialsFilePath) &&
            File.Exists(options.CredentialsFilePath)
                ? ValidateOptionsResult.Success
                : Invalid("FCM requires a project ID and an existing server-side credentials file.");
    }

    public ValidateOptionsResult Validate(string? name, QdrantOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        return TryGetServiceEndpoint(options.Endpoint, requireTls: false, out var endpoint) &&
            (endpoint.Scheme == Uri.UriSchemeHttps || IsPlaintextAllowed(endpoint.Host)) &&
            HasValue(options.ApiKey)
            ? ValidateOptionsResult.Success
            : Invalid("Qdrant requires a valid endpoint and API key when enabled.");
    }

    public ValidateOptionsResult Validate(string? name, MetaProviderOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        return string.Equals(options.Provider, "meta", StringComparison.OrdinalIgnoreCase) &&
            HasValue(options.RouteAlias) &&
            HasValue(options.ModelId) &&
            HasValue(options.ApiKey)
                ? ValidateOptionsResult.Success
                : Invalid("Meta requires an explicit provider, route, model, and server-side credential when enabled.");
    }

    public ValidateOptionsResult Validate(string? name, FederationOptions options)
    {
        var configuredProviders = new List<string>();
        var hasLegacyAuthority = !string.IsNullOrWhiteSpace(options.Authority) ||
            !string.IsNullOrWhiteSpace(options.ClientId) ||
            !string.IsNullOrWhiteSpace(options.ClientSecret);

        if (!options.Enabled && (options.Google.Enabled || options.Facebook.Enabled ||
                options.OrganizationProviders.Count > 0))
        {
            return Invalid("Federation:Enabled must be true when a child provider is enabled.");
        }

        if (hasLegacyAuthority)
        {
            return Invalid("Federation:Authority/ClientId/ClientSecret are legacy placeholders; configure a named provider instead.");
        }

        if (options.Google.Enabled)
        {
            if (!HasValue(options.Google.ClientId) ||
                !HasValue(options.Google.ClientSecret) ||
                !IsSafeGoogleCallbackPath(options.Google.CallbackPath))
            {
                return Invalid("Federation:Google requires server-side credentials and a safe local CallbackPath when enabled.");
            }

            configuredProviders.Add("google");
        }

        if (options.Facebook.Enabled)
        {
            if (!HasValue(options.Facebook.ClientId) || !HasValue(options.Facebook.ClientSecret))
            {
                return Invalid("Federation:Facebook requires server-side client credentials when enabled.");
            }

            configuredProviders.Add("facebook");
        }

        var webLogin = options.FacebookWebLogin;
        if (webLogin.Enabled &&
            (!options.Facebook.Enabled ||
             string.IsNullOrWhiteSpace(options.Facebook.ClientId) ||
             string.IsNullOrWhiteSpace(options.Facebook.ClientSecret) ||
             !HasStrongRateLimitHmacKey(webLogin.RateLimitHmacKeyBase64) ||
             !new[] { "v22.0", "v23.0", "v24.0", "v25.0" }.Contains(
                 webLogin.GraphApiVersion, StringComparer.Ordinal) ||
             webLogin.MaximumRequestBytes is < 256 or > 16384 ||
             webLogin.MaximumResponseBytes is < 1024 or > 65536 ||
             webLogin.OutboundTimeoutSeconds is < 1 or > 15 ||
             webLogin.MaximumConcurrentValidations is < 1 or > 64 ||
             webLogin.MaximumAttempts is < 1 or > 1000 ||
             webLogin.RateLimitWindowSeconds is < 1 or > 3600))
        {
            return Invalid("Federation:FacebookWebLogin requires Facebook credentials, an allowlisted Graph API version, a valid rate-limit key, and bounded request limits.");
        }

        var organizationIds = new HashSet<string>(StringComparer.Ordinal)
        {
            FederationSetup.GoogleProviderId,
            FederationSetup.FacebookProviderId
        };
        foreach (var provider in options.OrganizationProviders)
        {
            if (!IsProviderId(provider.ProviderId) || !organizationIds.Add(provider.ProviderId))
            {
                return Invalid("Federation:OrganizationProviders requires unique lowercase provider IDs.");
            }

            if (!TryGetServiceEndpoint(provider.Authority, requireTls: true, out _) ||
                !TryGetServiceEndpoint(provider.Issuer, requireTls: true, out _) ||
                !HasValue(provider.ClientId) || !HasValue(provider.ClientSecret) ||
                !HasValue(provider.TenantClaimType) || !HasValue(provider.TenantClaimValue))
            {
                return Invalid("Each enabled organization provider requires HTTPS authority/issuer, credentials, and an explicit tenant claim binding.");
            }

            configuredProviders.Add(provider.ProviderId);
        }

        return options.Enabled && configuredProviders.Count == 0
            ? Invalid("Federation is enabled but no named provider is configured.")
            : ValidateOptionsResult.Success;
    }

    private static bool HasStrongRateLimitHmacKey(string? configuredKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return false;
        }

        try
        {
            var keyBytes = Convert.FromBase64String(configuredKey);
            try
            {
                return keyBytes.Length >= 32;
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyBytes);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsSafeGoogleCallbackPath(string? path)
    {
        if (string.IsNullOrEmpty(path) ||
            path[0] != '/' ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = path[1..].Split('/');
        return segments.Length > 0 && segments.All(segment =>
            segment.Length > 0 && segment is not "." and not ".." &&
            segment.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~'));
    }

    public ValidateOptionsResult Validate(string? name, IdentityServerOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (!TryGetServiceEndpoint(options.Issuer, requireTls: true, out _) ||
            !Path.IsPathFullyQualified(options.SigningCertificatePath) ||
            !Path.IsPathFullyQualified(options.EncryptionCertificatePath) ||
            !File.Exists(options.SigningCertificatePath) ||
            !File.Exists(options.EncryptionCertificatePath) ||
            !HasValue(options.SigningCertificatePassword) ||
            !HasValue(options.EncryptionCertificatePassword) ||
            options.AuthorizationCodeLifetimeSeconds is < 30 or > 300 ||
            options.AccessTokenLifetimeSeconds is < 60 or > 3600 ||
            options.RefreshTokenLifetimeSeconds is < 3600 or > 7_776_000 ||
            options.PublicClients is not { Count: > 0 })
        {
            return Invalid("Identity requires an HTTPS issuer, existing server certificates, and at least one configured public client.");
        }

        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var client in options.PublicClients)
        {
            if (!HasValue(client.ClientId) || client.ClientId.Length > 128 ||
                !HasValue(client.DisplayName) || client.DisplayName.Length > 128 ||
                !clientIds.Add(client.ClientId) ||
                client.RedirectUris is not { Count: > 0 } ||
                client.RedirectUris.Distinct(StringComparer.Ordinal).Count() != client.RedirectUris.Count ||
                client.RedirectUris.Any(uri => !IsAllowedClientRedirect(uri)) ||
            client.PostLogoutRedirectUris is null ||
                client.PostLogoutRedirectUris.Distinct(StringComparer.Ordinal).Count() != client.PostLogoutRedirectUris.Count ||
            client.PostLogoutRedirectUris.Any(uri => !IsAllowedClientRedirect(uri)))
            {
                return Invalid("Identity:PublicClients must contain unique IDs, display names, and exact safe redirect URIs.");
            }
        }

        return ValidateOptionsResult.Success;
    }

    public ValidateOptionsResult Validate(string? name, SwiftAidDatabaseOptions options)
    {
        if (!HasValue(options.ConnectionString))
        {
            return ValidateOptionsResult.Success;
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(options.ConnectionString);
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException)
        {
            return Invalid("ConnectionStrings:SwiftAid is invalid.");
        }
    }

    private static ValidateOptionsResult Invalid(string message) =>
        ValidateOptionsResult.Fail(message);

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool IsProviderId(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool TryGetMinioEndpoint(string? value, bool useSsl, out Uri endpoint)
    {
        if (!TryGetServiceEndpoint(value, useSsl, out endpoint))
        {
            return false;
        }

        return (endpoint.AbsolutePath.Length == 0 || endpoint.AbsolutePath == "/") &&
            endpoint.Port > 0 &&
            (endpoint.Scheme == Uri.UriSchemeHttps) == useSsl;
    }

    private static bool TryGetServiceEndpoint(string? value, bool requireTls, out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            (requireTls && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private static bool IsAllowedClientRedirect(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return (uri.Scheme == Uri.UriSchemeHttp &&
                IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)) ||
            (uri.Scheme == Uri.UriSchemeHttp && string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(uri.Scheme, "swiftaid", StringComparison.Ordinal) ||
            string.Equals(uri.Scheme, "online.swiftaid.mobile", StringComparison.Ordinal);
    }

    private static bool TryGetNatsEndpoint(string? value, out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != "nats" && parsed.Scheme != "tls") ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private bool IsPlaintextAllowed(string host) =>
        _hostEnvironment is not null &&
        string.Equals(
            _hostEnvironment.EnvironmentName,
            Environments.Development,
            StringComparison.OrdinalIgnoreCase) &&
        _transportSecurityOptions.AllowPlaintextDevelopment &&
        _transportSecurityOptions.AllowedDevelopmentHosts is { } allowedHosts &&
        allowedHosts.Any(allowedHost => HostsMatch(host, allowedHost));

    private bool ArePlaintextRedisEndpointsAllowed(ConfigurationOptions options)
    {
        var endpoints = options.EndPoints.ToArray();
        return endpoints.Length > 0 && endpoints.All(endpoint => endpoint switch
        {
            DnsEndPoint dnsEndpoint => IsPlaintextAllowed(dnsEndpoint.Host),
            IPEndPoint ipEndpoint => IsPlaintextAllowed(ipEndpoint.Address.ToString()),
            _ => false
        });
    }

    private static bool HostsMatch(string host, string? allowedHost) =>
        !string.IsNullOrWhiteSpace(allowedHost) &&
        string.Equals(NormalizeHost(host), NormalizeHost(allowedHost), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHost(string host) =>
        host.Trim().TrimStart('[').TrimEnd(']');

    private static bool IsValidBucket(string? bucket)
    {
        if (string.IsNullOrEmpty(bucket) || bucket.Length is < 3 or > 63)
        {
            return false;
        }

        return bucket.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '-') &&
            !bucket.StartsWith(".", StringComparison.Ordinal) &&
            !bucket.EndsWith(".", StringComparison.Ordinal) &&
            !bucket.StartsWith("-", StringComparison.Ordinal) &&
            !bucket.EndsWith("-", StringComparison.Ordinal) &&
            !bucket.Contains("..", StringComparison.Ordinal);
    }

    private static bool IsStreamName(string? name) =>
        name is { Length: >= 1 and <= 64 } &&
        name.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_');

    private static bool IsSubjectPrefix(string? subject) =>
        subject is { Length: > 0 } && subject.Split('.').All(IsSubjectToken);

    private static bool IsSubject(string? subject) => IsSubjectPrefix(subject);

    private static bool IsSubjectToken(string token) =>
        token.Length > 0 &&
        token.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
