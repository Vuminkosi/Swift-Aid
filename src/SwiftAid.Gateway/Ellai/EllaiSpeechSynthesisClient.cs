using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SwiftAid.Gateway.Ellai;

public interface IEllaiSpeechSynthesisClient
{
    bool IsConfigured { get; }

    Task<HttpResponseMessage> SynthesizeAsync(
        string requestId,
        string text,
        CancellationToken cancellationToken);

    Task<HttpResponseMessage> SynthesizeAsync(
        string requestId, string text, string language, string? voiceId,
        CancellationToken cancellationToken) => language == "eng" && voiceId is null or "default"
            ? SynthesizeAsync(requestId, text, cancellationToken)
            : throw new InvalidOperationException("The requested speech profile is unavailable.");
}

internal static class EllaiSpeechResponseContract
{
    public static bool IsValidPcmResponse(HttpResponseMessage response) =>
        response.IsSuccessStatusCode
        && string.Equals(response.Content.Headers.ContentType?.MediaType,
            "application/vnd.swiftaid.pcm", StringComparison.OrdinalIgnoreCase)
        && response.Headers.Contains("X-SwiftAid-Voice-Id")
        && response.Headers.Contains("X-SwiftAid-Sample-Rate");
}

/// <summary>Server-side client for the private Gateway voice engine. Text and
/// credentials are never sent to clients or written to logs.</summary>
public sealed class EllaiSpeechSynthesisClient : IEllaiSpeechSynthesisClient
{
    private readonly HttpClient _httpClient;
    private readonly string _internalKey;

    public EllaiSpeechSynthesisClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(configuration);

        _internalKey = configuration["EllaiTts:InternalKey"] ?? string.Empty;
        var endpoint = configuration["EllaiTts:Endpoint"];
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            && (string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            _httpClient.BaseAddress = endpointUri;
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        }
    }

    public bool IsConfigured => _httpClient.BaseAddress is not null
        && !string.IsNullOrWhiteSpace(_internalKey);

    public Task<HttpResponseMessage> SynthesizeAsync(
        string requestId,
        string text,
        CancellationToken cancellationToken) =>
        SynthesizeAsync(requestId, text, "eng", null, cancellationToken);

    public Task<HttpResponseMessage> SynthesizeAsync(
        string requestId, string text, string language, string? voiceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (language is not ("eng" or "sna" or "nya" or "tso")
            || (voiceId is not null && voiceId != "default" && (language != "eng" || voiceId != "af_heart")))
        {
            throw new ArgumentException("The requested speech profile is unavailable.");
        }
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Gateway speech synthesis is not configured.");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "v1/synthesize")
        {
            Content = JsonContent.Create(
                new EllaiSpeechSynthesisRequest(requestId, text, language, voiceId ?? "default"),
                options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _internalKey);
        return _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private sealed record EllaiSpeechSynthesisRequest(string RequestId, string Text, string Language, string VoiceId);
}
