using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Bounded, identity-checked private inference. Never exposes provider diagnostics or payloads.</summary>
public sealed class EllaiLanguageClient : IEllaiLanguageClient
{
    public const string TranslationModel = "facebook/nllb-200-distilled-1.3B";
    public const string TranslationRevision = "7be3e24664b38ce1cac29b8aeed6911aa0cf0576";
    public const string TranscriptionModel = "facebook/mms-1b-all";
    public const string TranscriptionRevision = "3d33597edbdaaba14a8e858e2c8caa76e3cec0cd";
    private readonly HttpClient _http;
    private readonly string _key;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public EllaiLanguageClient(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _key = configuration["EllaiLanguage:InternalKey"] ?? string.Empty;
        if (Uri.TryCreate(configuration["EllaiLanguage:Endpoint"], UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https")
            _http.BaseAddress = endpoint;
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    public bool IsConfigured => _http.BaseAddress is not null && !string.IsNullOrWhiteSpace(_key);

    public async Task<EllaiTranslationResult> TranslateAsync(EllaiTranslationRequest request, CancellationToken cancellationToken)
    {
        ValidateId(request.RequestId);
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 6000 ||
            !Supported(request.SourceLanguage) || !Supported(request.TargetLanguage) ||
            request.SourceLanguage == request.TargetLanguage ||
            (request.SourceLanguage != "eng" && request.TargetLanguage != "eng"))
            throw new ArgumentException("Unsupported translation request.");
        var result = await SendAsync<EllaiTranslationResult>("v1/translate", request, cancellationToken);
        if (result.RequestId != request.RequestId || result.SourceLanguage != request.SourceLanguage ||
            result.TargetLanguage != request.TargetLanguage || result.ModelId != TranslationModel ||
            result.ModelRevision != TranslationRevision || result.Quality != "unreviewed" ||
            string.IsNullOrWhiteSpace(result.Text) || result.Text.Length > 16000 || result.InputTokens is < 1 or > 512 ||
            !double.IsFinite(result.InferenceMilliseconds) || result.InferenceMilliseconds < 0)
            throw new InvalidDataException("Invalid translation provider result.");
        return result;
    }

    public async Task<EllaiNativeTranscriptionResult> TranscribeAsync(EllaiNativeTranscriptionRequest request, CancellationToken cancellationToken)
    {
        ValidateId(request.RequestId);
        if (request.Language is not ("sna" or "nya" or "tso") || request.SampleRate != 16000 ||
            string.IsNullOrWhiteSpace(request.PcmBase64) || request.PcmBase64.Length > 2_560_000)
            throw new ArgumentException("Unsupported transcription request.");
        var result = await SendAsync<EllaiNativeTranscriptionResult>("v1/transcribe", request, cancellationToken);
        if (result.RequestId != request.RequestId || result.Language != request.Language ||
            result.ModelId != TranscriptionModel || result.ModelRevision != TranscriptionRevision ||
            result.Quality != "unreviewed" || result.Text is null || result.Text.Length > 16000 ||
            !double.IsFinite(result.InferenceMilliseconds) || result.InferenceMilliseconds < 0)
            throw new InvalidDataException("Invalid transcription provider result.");
        return result;
    }

    private async Task<T> SendAsync<T>(string path, object payload, CancellationToken ct)
    {
        if (!IsConfigured) throw new EllaiLanguageProviderException("language-provider-unconfigured", false);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload, options: Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new EllaiLanguageProviderException("language-provider-rejected",
                    response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500);
            if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength > 131072)
                throw new InvalidDataException("Invalid language provider response.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (body.Length + count > 131072) throw new InvalidDataException("Language provider response exceeds limit.");
                body.Write(buffer, 0, count);
            }
            return JsonSerializer.Deserialize<T>(body.ToArray(), Json) ?? throw new InvalidDataException("Missing language provider result.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EllaiLanguageProviderException("language-provider-timeout", true);
        }
        catch (HttpRequestException)
        {
            throw new EllaiLanguageProviderException("language-provider-unavailable", true);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Invalid language provider result.");
        }
    }

    private static bool Supported(string language) => language is "eng" or "sna" or "nya" or "tso" or "ssw";
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 120) throw new ArgumentException("Invalid language request identity.");
    }
}
