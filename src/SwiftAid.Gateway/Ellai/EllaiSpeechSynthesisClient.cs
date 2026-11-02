using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

public interface IEllaiSpeechSynthesisClient
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<EllaiEnglishVoiceDto>> ListEnglishVoicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EllaiEnglishVoiceDto>>([]);

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
    // Typed HttpClient instances are transient, but the configured local engine has
    // one inference owner. Hold only through response headers: PCM delivery and
    // playback can continue while the next sentence is synthesized.
    private static readonly SemaphoreSlim InferenceGate = new(1, 1);
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

    public async Task<IReadOnlyList<EllaiEnglishVoiceDto>> ListEnglishVoicesAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) return [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/voices");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _internalKey);
        try
        {
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return [];
            var voices = await response.Content.ReadFromJsonAsync<EllaiEnglishVoiceDto[]>(timeout.Token);
            return (voices ?? []).Where(voice => voice.Language == "eng"
                && EllaiLanguagePreferences.IsSupportedEnglishVoice(voice.VoiceId))
                .DistinctBy(voice => voice.VoiceId).Take(8).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return []; }
        catch (HttpRequestException) { return []; }
        catch (JsonException) { return []; }
    }

    public Task<HttpResponseMessage> SynthesizeAsync(
        string requestId,
        string text,
        CancellationToken cancellationToken) =>
        SynthesizeAsync(requestId, text, "eng", null, cancellationToken);

    public async Task<HttpResponseMessage> SynthesizeAsync(
        string requestId, string text, string language, string? voiceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (language is not ("eng" or "sna" or "nya" or "tso")
            || (voiceId is not null && voiceId != "default" && (language != "eng"
                || !SwiftAid.Contracts.Ellai.EllaiLanguagePreferences.IsSupportedEnglishVoice(voiceId))))
        {
            throw new ArgumentException("The requested speech profile is unavailable.");
        }
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Gateway speech synthesis is not configured.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var acquired = false;
        try
        {
            await InferenceGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "v1/synthesize")
                {
                    Content = JsonContent.Create(
                        new EllaiSpeechSynthesisRequest(requestId, text, language, voiceId ?? "default"),
                        options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _internalKey);
                var response = await _httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                // A busy rejection has produced no PCM; retrying it cannot repeat
                // delivered speech. Never retry after a successful audio stream starts.
                if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt >= 2)
                    return response;
                var delay = TimeSpan.FromMilliseconds(Math.Clamp(
                    response.Headers.RetryAfter?.Delta?.TotalMilliseconds ?? 1000, 100, 2000));
                response.Dispose();
                await Task.Delay(delay, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("Speech synthesis timed out.", error);
        }
        finally
        {
            if (acquired) InferenceGate.Release();
        }
    }

    private sealed record EllaiSpeechSynthesisRequest(string RequestId, string Text, string Language, string VoiceId);
}
