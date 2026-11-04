namespace SwiftAid.Infrastructure.Identity;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SwiftAid.Infrastructure.Configuration;

public enum FacebookTokenValidationStatus
{
    Valid,
    Invalid,
    Unavailable
}

public sealed record FacebookTokenValidationResult(
    FacebookTokenValidationStatus Status,
    string? Subject = null);

public interface IFacebookWebTokenValidator
{
    ValueTask<FacebookTokenValidationResult> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken);
}

/// <summary>
/// Sends a Meta debug-token request through a dedicated invoker. The request
/// carries a sanitized URI outside the final handler; its credential-bearing URI
/// is substituted for transport and restored before control returns.
/// </summary>
public sealed class FacebookWebTokenValidator : IFacebookWebTokenValidator, IDisposable
{
    private const string GraphHost = "graph.facebook.com";
    private static readonly HashSet<string> AllowedGraphVersions = new(StringComparer.Ordinal)
    {
        "v22.0", "v23.0", "v24.0", "v25.0"
    };

    private readonly FederationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly HttpMessageInvoker _invoker;
    private readonly SemaphoreSlim _concurrency;

    public FacebookWebTokenValidator(FederationOptions options, TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _concurrency = new SemaphoreSlim(Math.Clamp(
            options.FacebookWebLogin.MaximumConcurrentValidations, 1, 64));
        var sockets = new SocketsHttpHandler
        {
            ActivityHeadersPropagator = null,
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            MaxResponseHeadersLength = 16,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(
                options.FacebookWebLogin.OutboundTimeoutSeconds, 1, 15)),
            MaxConnectionsPerServer = Math.Clamp(
                options.FacebookWebLogin.MaximumConcurrentValidations, 1, 64),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        _invoker = new HttpMessageInvoker(
            new SensitiveMetaQueryHandler(sockets),
            disposeHandler: true);
    }

    public async ValueTask<FacebookTokenValidationResult> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 4096 ||
            !AllowedGraphVersions.Contains(_options.FacebookWebLogin.GraphApiVersion) ||
            string.IsNullOrWhiteSpace(_options.Facebook.ClientId) ||
            string.IsNullOrWhiteSpace(_options.Facebook.ClientSecret))
        {
            return new FacebookTokenValidationResult(FacebookTokenValidationStatus.Invalid);
        }

        var entered = false;
        try
        {
            entered = await _concurrency.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!entered)
            {
                return new FacebookTokenValidationResult(FacebookTokenValidationStatus.Unavailable);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(
                _options.FacebookWebLogin.OutboundTimeoutSeconds, 1, 15)));
            var applicationToken = string.Concat(
                _options.Facebook.ClientId, "|", _options.Facebook.ClientSecret);
            var credentialUri = new Uri(string.Concat(
                "https://", GraphHost, "/", _options.FacebookWebLogin.GraphApiVersion,
                "/debug_token?input_token=", Uri.EscapeDataString(accessToken),
                "&access_token=", Uri.EscapeDataString(applicationToken)), UriKind.Absolute);
            using var request = new FacebookDebugTokenRequest(
                new Uri($"https://{GraphHost}/{_options.FacebookWebLogin.GraphApiVersion}/debug_token", UriKind.Absolute),
                credentialUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden)
            {
                return new FacebookTokenValidationResult(FacebookTokenValidationStatus.Invalid);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new FacebookTokenValidationResult(FacebookTokenValidationStatus.Unavailable);
            }

            if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase))
            {
                return new FacebookTokenValidationResult(FacebookTokenValidationStatus.Unavailable);
            }

            var maximumBytes = Math.Clamp(_options.FacebookWebLogin.MaximumResponseBytes, 1024, 65536);
            if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
            {
                return new FacebookTokenValidationResult(FacebookTokenValidationStatus.Unavailable);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var bytes = new byte[maximumBytes + 1];
            try
            {
                var total = 0;
                while (total < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(total), timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                if (total > maximumBytes)
                {
                    return new FacebookTokenValidationResult(FacebookTokenValidationStatus.Unavailable);
                }

                return TryReadSubject(bytes.AsMemory(0, total), out var subject)
                    ? new FacebookTokenValidationResult(FacebookTokenValidationStatus.Valid, subject)
                    : new FacebookTokenValidationResult(FacebookTokenValidationStatus.Invalid);
            }
            catch (JsonException)
            {
                return new FacebookTokenValidationResult(FacebookTokenValidationStatus.Unavailable);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new FacebookTokenValidationResult(FacebookTokenValidationStatus.Unavailable);
        }
        finally
        {
            if (entered)
            {
                _concurrency.Release();
            }
        }
    }

    public void Dispose()
    {
        _invoker.Dispose();
        _concurrency.Dispose();
    }

    private bool TryReadSubject(ReadOnlyMemory<byte> json, out string subject)
    {
        subject = string.Empty;
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        if (!TryGetString(data, "app_id", out var appId) ||
            !string.Equals(appId, _options.Facebook.ClientId, StringComparison.Ordinal) ||
            !TryGetString(data, "type", out var type) ||
            !string.Equals(type, "USER", StringComparison.Ordinal) ||
            !data.TryGetProperty("is_valid", out var valid) || valid.ValueKind != JsonValueKind.True ||
            !data.TryGetProperty("expires_at", out var expiryElement) ||
            !expiryElement.TryGetInt64(out var expirySeconds) || expirySeconds <= 0 ||
            !TryGetString(data, "user_id", out subject) || subject.Length > 1024 ||
            !data.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        DateTimeOffset expiry;
        try
        {
            expiry = DateTimeOffset.FromUnixTimeSeconds(expirySeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return expiry > _timeProvider.GetUtcNow() && scopes.EnumerateArray().Any(scope =>
            scope.ValueKind == JsonValueKind.String &&
            string.Equals(scope.GetString(), "public_profile", StringComparison.Ordinal));
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private sealed class FacebookDebugTokenRequest(Uri sanitizedUri, Uri credentialUri) : HttpRequestMessage(HttpMethod.Get, sanitizedUri)
    {
        public Uri CredentialUri { get; } = credentialUri;
    }

    private sealed class SensitiveMetaQueryHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request is not FacebookDebugTokenRequest sensitiveRequest)
            {
                throw new InvalidOperationException("Unexpected Meta transport request.");
            }

            var sanitizedUri = request.RequestUri!;
            try
            {
                request.RequestUri = sensitiveRequest.CredentialUri;
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                request.RequestUri = sanitizedUri;
            }
        }
    }
}
