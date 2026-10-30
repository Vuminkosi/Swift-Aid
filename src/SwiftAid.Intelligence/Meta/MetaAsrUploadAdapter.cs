using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Single diarized turn from the transcription route.</summary>
public sealed record AsrTurn(int TurnId, long StartMs, long EndMs, string Transcript, string Speaker);

/// <summary>Verified whole-recording transcription result.</summary>
public sealed record AsrTranscriptionResult(string SessionId, string Transcript, long AudioDurationMs, IReadOnlyList<AsrTurn> Turns);

/// <summary>Live Meta whole-recording transcription adapter (ELL-T11).
/// Multipart upload with <c>request</c> (JSON settings) and <c>audio</c> parts
/// per the official voice reference; Authorization header only. The key never
/// appears in exceptions, logs or results. WAV recordings only, 10-minute /
/// 32 MB provider caps enforced client-side first.</summary>
public sealed class MetaAsrUploadAdapter(HttpClient http, EllaiOptions options)
{
    public const long MaxAudioBytes = 32 * 1024 * 1024;

    public async Task<AsrTranscriptionResult> TranscribeAsync(
        Stream audio, string fileName, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (!options.HasUploadAsrRoute)
        {
            throw new MetaConfigurationException("Ellai ASR upload route is not configured.");
        }

        if (audio.CanSeek && audio.Length > MaxAudioBytes)
        {
            throw new ArgumentException("Recording exceeds the 32 MB provider cap.");
        }

        var settings = JsonSerializer.Serialize(new
        {
            model = options.AsrModel,
            audioEncoding = "WAV",
            mode = options.AsrMode,
            language = options.AsrLanguage
        });

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(settings, Encoding.UTF8, "application/json"), "request");
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(audioContent, "audio", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, options.AsrTranscriptionUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = content;

        int status;
        string body;
        try
        {
            using var response = await http.SendAsync(request, ct);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Ellai ASR transport failed ({ex.GetType().Name}).");
        }

        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"Ellai ASR refused the request (HTTP {status}).");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var sessionId = root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? string.Empty : string.Empty;
            var transcript = root.TryGetProperty("transcript", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? string.Empty : string.Empty;
            var duration = root.TryGetProperty("audioDurationMs", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var ms)
                ? ms : 0;
            var turns = new List<AsrTurn>();
            if (root.TryGetProperty("turns", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    turns.Add(new AsrTurn(
                        GetInt(item, "turnId"), GetLong(item, "startMs"), GetLong(item, "endMs"),
                        GetString(item, "transcript"), GetString(item, "speaker")));
                }
            }

            return new AsrTranscriptionResult(sessionId, transcript, duration, turns);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException("Ellai ASR returned an unreadable envelope.");
        }
    }

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
