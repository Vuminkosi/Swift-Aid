using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace SwiftAid.Infrastructure.Configuration;

/// <summary>Validates enabled provider settings without connecting to any provider.</summary>
public sealed class ProviderOptionsValidator :
    IValidateOptions<MinioOptions>,
    IValidateOptions<RedisOptions>,
    IValidateOptions<NatsOptions>,
    IValidateOptions<FcmOptions>,
    IValidateOptions<QdrantOptions>,
    IValidateOptions<MetaProviderOptions>,
    IValidateOptions<FederationOptions>,
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

        if (endpoint.Scheme == "nats" && !IsPlaintextAllowed(endpoint.Host))
        {
            return Invalid("Nats:Url must use TLS unless an allowlisted Development plaintext exception applies.");
        }

        return ValidateOptionsResult.Success;
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
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        return TryGetServiceEndpoint(options.Authority, requireTls: true, out _) &&
            HasValue(options.ClientId) &&
            HasValue(options.ClientSecret)
                ? ValidateOptionsResult.Success
                : Invalid("Federation requires an HTTPS authority and server-side client credentials when enabled.");
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
