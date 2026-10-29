using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Presentation;

namespace SwiftAid.Mobile.Services.Http;

/// <summary>
/// Cookie-session HTTP transport shared by cookie-authenticated mobile calls.
/// One jar per gateway address; sign-out clears it via <see cref="ClearCookies"/>.
/// </summary>
public sealed class CookieSessionHttpClient : IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CookieSessionHttpClient> SharedByAddress = new(
        StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _sync = new();
    private HttpClientHandler _handler;
    private HttpClient _client;
    private bool _disposed;

    public CookieSessionHttpClient(string? baseAddress)
    {
        (_handler, _client) = CreateClient(baseAddress);
    }

    private static (HttpClientHandler Handler, HttpClient Client) CreateClient(string? baseAddress)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer()
        };
        var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (Uri.TryCreate(baseAddress, UriKind.Absolute, out var baseUri))
        {
            client.BaseAddress = baseUri;
        }

        return (handler, client);
    }

    public static CookieSessionHttpClient Shared(string? baseAddress) =>
        SharedByAddress.GetOrAdd(Normalize(baseAddress), static key => new CookieSessionHttpClient(key));

    public void ClearCookies()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var baseAddress = _client.BaseAddress?.ToString();
            _client.Dispose();
            _handler.Dispose();
            (_handler, _client) = CreateClient(baseAddress);
        }
    }

    public async Task<string?> GetAntiforgeryTokenAsync(CancellationToken cancellationToken = default)
    {
        HttpClient client;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _client;
        }

        if (client.BaseAddress is null)
        {
            return null;
        }

        using var response = await client.GetAsync("/auth/antiforgery", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("requestToken", out var token)
            ? token.GetString()
            : null;
    }

    public async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        HttpClient client;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _client;
        }

        return await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> PutJsonAsync<T>(
        string path,
        T body,
        string? antiforgeryToken,
        CancellationToken cancellationToken = default)
    {
        HttpClient client;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _client;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(body, MediaTypeHeaderValue.Parse("application/json"), JsonOptions)
        };
        if (!string.IsNullOrWhiteSpace(antiforgeryToken))
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", antiforgeryToken);
        }

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
        _handler.Dispose();
    }

    private static string Normalize(string? baseAddress) =>
        string.IsNullOrWhiteSpace(baseAddress) ? string.Empty : baseAddress.Trim().TrimEnd('/');
}
