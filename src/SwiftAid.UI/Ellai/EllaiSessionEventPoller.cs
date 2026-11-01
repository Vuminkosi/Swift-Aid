using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.UI.Ellai;

/// <summary>HTTP review client for the gateway Ellai endpoints: event
/// long-poll, active-review fetch, approve-selected and reject. Relative
/// URLs: the host app supplies the authorized HttpClient. Non-success
/// responses read as unavailable so the caller keeps polling.</summary>
public sealed class EllaiReviewHttpClient(HttpClient http, bool useGuestRoutes = false) : IEllaiReviewClient
{
    private const int MaximumPcmBytes = 3_840_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly bool _useGuestRoutes = useGuestRoutes;
    private string? _guestAntiforgeryToken;
    private string? _responseStreamId;

    public async Task<EllaiSessionHistoryPageDto?> ListSessionHistoryAsync(int offset, CancellationToken cancellationToken)
    {
        try
        {
            var route = _useGuestRoutes ? "api/v1/ellai/guest/sessions" : "api/v1/ellai/sessions";
            using var response = await _http.GetAsync($"{route}?offset={Math.Max(0, offset)}", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<EllaiSessionHistoryPageDto>(Json, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    public Uri CreateLiveVoiceWebSocketUri(Uri baseUri, string sessionId, string voiceCaptureId,
        long voiceCaptureOffsetSamples = 0)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceCaptureId);
        var route = $"{SessionRoute(sessionId)}/voice/live-stream?voiceCaptureId={Uri.EscapeDataString(voiceCaptureId)}&serverSubmission=true";
        if (voiceCaptureOffsetSamples > 0)
        {
            route += $"&voiceCaptureOffsetSamples={voiceCaptureOffsetSamples}";
        }

        var uri = new Uri(baseUri, route);
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };
        return builder.Uri;
    }

    public async Task<EllaiSessionStartResult> StartSessionAsync(
        EllaiMode mode, CancellationToken cancellationToken)
    {
        try
        {
            var request = new StartSessionRequest(
                mode, Guid.NewGuid().ToString("N"), null, null, null, null);
            using var response = await PostAsJsonAsync(
                _useGuestRoutes ? "api/v1/ellai/guest/sessions" : "api/v1/ellai/sessions",
                request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new(MapStartStatus(response.StatusCode));
            }

            var session = await response.Content
                .ReadFromJsonAsync<EllaiSessionView>(Json, cancellationToken)
                .ConfigureAwait(false);
            return session is null || string.IsNullOrWhiteSpace(session.SessionId)
                ? new(EllaiClientOperationStatus.OutcomeUnknown)
                : new(EllaiClientOperationStatus.Accepted, session);
        }
        catch (OperationCanceledException)
        {
            return new(EllaiClientOperationStatus.OutcomeUnknown);
        }
        catch (Exception)
        {
            return new(EllaiClientOperationStatus.OutcomeUnknown);
        }
    }

    private static EllaiClientOperationStatus MapStartStatus(System.Net.HttpStatusCode status)
    {
        if ((int)status >= 500)
        {
            return EllaiClientOperationStatus.OutcomeUnknown;
        }

        return status switch
        {
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                => EllaiClientOperationStatus.Unauthorized,
            System.Net.HttpStatusCode.BadRequest
                => EllaiClientOperationStatus.InvalidRequest,
            System.Net.HttpStatusCode.UnprocessableEntity
                => EllaiClientOperationStatus.PolicyBlocked,
            _ => EllaiClientOperationStatus.Unavailable,
        };
    }

    public async Task<EllaiSessionStartResult> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(SessionRoute(sessionId), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? EllaiClientOperationStatus.Unauthorized : MapStartStatus(response.StatusCode));
            var session = await response.Content.ReadFromJsonAsync<EllaiSessionView>(Json, cancellationToken).ConfigureAwait(false);
            return session is null ? new(EllaiClientOperationStatus.OutcomeUnknown)
                : new(EllaiClientOperationStatus.Accepted, session);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(EllaiClientOperationStatus.OutcomeUnknown);
        }
    }

    public async Task<EllaiUtteranceSubmitResult> SubmitUtteranceAsync(
        SubmitUtteranceRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return new(EllaiClientOperationStatus.InvalidRequest, null);
        }

        try
        {
            using var response = await PostAsJsonAsync(
                $"{SessionRoute(request.SessionId)}/utterances", request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                EllaiSessionView? current = null;
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    try
                    {
                        current = await response.Content.ReadFromJsonAsync<EllaiSessionView>(Json, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (JsonException) { }
                }
                return new(MapStatus(response.StatusCode), current);
            }

            var session = await response.Content
                .ReadFromJsonAsync<EllaiSessionView>(Json, cancellationToken)
                .ConfigureAwait(false);
            return session is null
                ? new(EllaiClientOperationStatus.Unavailable, null)
                : new(EllaiClientOperationStatus.Accepted, session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new(EllaiClientOperationStatus.Unavailable, null);
        }
    }

    public async Task<bool> GrantPublicProductHelpConsentAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        try
        {
            using var response = await PostAsJsonAsync(
                $"{SessionRoute(sessionId)}/consents",
                new
                {
                    SessionId = sessionId,
                    Dimension = "ReasoningText",
                    Purpose = EllaiConsentPurposes.PublicProductHelp,
                    HoursToLive = 1,
                }, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> GrantVoiceCaptureConsentAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        foreach (var dimension in new[] { "Microphone", "Transcription" })
        {
            try
            {
                using var response = await PostAsJsonAsync(
                    $"{SessionRoute(sessionId)}/consents",
                    new
                    {
                        SessionId = sessionId,
                        Dimension = dimension,
                        Purpose = EllaiConsentPurposes.VoiceTranscription,
                        HoursToLive = 1,
                    }, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
        }

        return true;
    }

    public async Task<EllaiVoiceTranscriptionResult?> TranscribeVoiceAsync(
        string sessionId, byte[] audio, EllaiVoiceCaptureMode mode, CancellationToken cancellationToken,
        string? voiceCaptureId = null, long voiceCaptureOffsetSamples = 0)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || audio is null || audio.Length == 0
            || audio.Length > 32 * 1024 * 1024)
        {
            return null;
        }

        var route = $"{SessionRoute(sessionId)}/voice/{mode.ToString().ToLowerInvariant()}?serverSubmission=true";
        if (!string.IsNullOrWhiteSpace(voiceCaptureId) || voiceCaptureOffsetSamples > 0)
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(voiceCaptureId))
            {
                query.Add($"voiceCaptureId={Uri.EscapeDataString(voiceCaptureId)}");
            }
            if (voiceCaptureOffsetSamples > 0)
            {
                query.Add($"voiceCaptureOffsetSamples={voiceCaptureOffsetSamples}");
            }
            route += $"&{string.Join('&', query)}";
        }
        var contentType = mode == EllaiVoiceCaptureMode.Recorded ? "audio/wav" : "application/octet-stream";
        try
        {
            using var response = await PostBytesAsync(route, audio, contentType, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<EllaiVoiceTranscriptionResult>(Json, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<EllaiVoiceTranscriptionResult?> TranscribeLiveVoiceStreamAsync(
        string sessionId, Stream audio, CancellationToken cancellationToken, string? voiceCaptureId = null,
        long voiceCaptureOffsetSamples = 0)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || audio is null || !audio.CanRead)
        {
            return null;
        }

        var content = new StreamContent(audio, 8_192);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var route = $"{SessionRoute(sessionId)}/voice/live";
        var query = new List<string> { "serverSubmission=true" };
        if (!string.IsNullOrWhiteSpace(voiceCaptureId))
        {
            query.Add($"voiceCaptureId={Uri.EscapeDataString(voiceCaptureId)}");
        }
        if (voiceCaptureOffsetSamples > 0)
        {
            query.Add($"voiceCaptureOffsetSamples={voiceCaptureOffsetSamples}");
        }
        if (query.Count > 0)
        {
            route += $"?{string.Join('&', query)}";
        }
        using (content)
        using (var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = content })
        {
            if (_useGuestRoutes)
            {
                var token = await GetGuestAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return null;
                }
                request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
            }

            try
            {
                using var response = await _http.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode
                    ? await response.Content.ReadFromJsonAsync<EllaiVoiceTranscriptionResult>(Json, cancellationToken)
                        .ConfigureAwait(false)
                    : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public async Task<EllaiVoiceTurnStatusDto?> GetVoiceTurnStatusAsync(
        string sessionId, CancellationToken cancellationToken, long afterSequence = 0)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(
                $"{SessionRoute(sessionId)}/voice/state?afterSequence={afterSequence}", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<EllaiVoiceTurnStatusDto>(Json, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<EllaiSessionCancelResult> CancelSessionAsync(
        string sessionId, long expectedSessionVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || expectedSessionVersion < 0)
            return new(EllaiClientOperationStatus.InvalidRequest);
        try
        {
            using var response = await PostAsJsonAsync(
                $"{SessionRoute(sessionId)}/cancel", new { expectedSessionVersion }, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return new(EllaiClientOperationStatus.Cancelled);
            EllaiSessionView? current = null;
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                try
                {
                    current = await response.Content.ReadFromJsonAsync<EllaiSessionView>(Json, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (JsonException) { }
            }
            return new(MapStatus(response.StatusCode), current);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(EllaiClientOperationStatus.Unavailable); }
    }

    private static EllaiClientOperationStatus MapStatus(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => EllaiClientOperationStatus.Unauthorized,
        System.Net.HttpStatusCode.NotFound => EllaiClientOperationStatus.Unauthorized,
        System.Net.HttpStatusCode.BadRequest => EllaiClientOperationStatus.InvalidRequest,
        System.Net.HttpStatusCode.Conflict => EllaiClientOperationStatus.Conflict,
        System.Net.HttpStatusCode.UnprocessableEntity => EllaiClientOperationStatus.PolicyBlocked,
        _ => EllaiClientOperationStatus.Unavailable,
    };

    public async Task<EllaiSessionEventPageDto?> PollEventsAsync(
        string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_useGuestRoutes)
        {
            waitSeconds = 0;
        }
        var url = $"{SessionRoute(sessionId)}/events" +
            $"?cursor={Math.Max(0, cursor)}&waitSeconds={Math.Clamp(waitSeconds, 0, 30)}";
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<EllaiSessionEventPageDto>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<EllaiSessionResponsePageDto?> PollResponsesAsync(
        string sessionId, long cursor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        try
        {
            using var response = await _http.GetAsync(
                $"{SessionRoute(sessionId)}/responses?cursor={Math.Max(0, cursor)}&streamId={Uri.EscapeDataString(_responseStreamId ?? string.Empty)}",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var page = await response.Content
                .ReadFromJsonAsync<EllaiSessionResponsePageDto>(Json, cancellationToken)
                .ConfigureAwait(false);
            _responseStreamId = page?.StreamId;
            return page;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<EllaiResponseAudio?> DownloadResponseAudioAsync(
        string sessionId, long sequence, CancellationToken cancellationToken)
        => await DownloadResponseAudioAsync(sessionId, sequence, string.Empty, cancellationToken).ConfigureAwait(false);

    public async Task<EllaiResponseAudio?> DownloadResponseAudioAsync(
        string sessionId, long sequence, string playbackId, CancellationToken cancellationToken)
    {
        if (sequence < 1)
        {
            return null;
        }

        return await DownloadPcmAudioAsync(
            sessionId, $"{SessionRoute(sessionId)}/responses/{sequence}/audio"
                + (Guid.TryParseExact(playbackId, "N", out _) ? $"?playbackId={Uri.EscapeDataString(playbackId)}" : string.Empty), sequence,
            expectedPurpose: null, expectedPlaybackId: playbackId, cancellationToken).ConfigureAwait(false);
    }

    public Task<EllaiResponseAudioStream?> DownloadResponseAudioStreamAsync(
        string sessionId, long sequence, string playbackId, CancellationToken cancellationToken)
    {
        if (sequence < 1 || string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult<EllaiResponseAudioStream?>(null);
        }

        var requestUri = $"{SessionRoute(sessionId)}/responses/{sequence}/audio"
            + (Guid.TryParseExact(playbackId, "N", out _)
                ? $"?playbackId={Uri.EscapeDataString(playbackId)}"
                : string.Empty);
        return DownloadPcmAudioStreamAsync(
            sessionId, requestUri, sequence, playbackId, cancellationToken);
    }

    private async Task<EllaiResponseAudioStream?> DownloadPcmAudioStreamAsync(
        string sessionId,
        string requestUri,
        long expectedSequence,
        string? expectedPlaybackId,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await _http.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType != "application/vnd.swiftaid.pcm"
                || !response.Headers.TryGetValues("X-SwiftAid-Session-Id", out var sessionIds)
                || !sessionIds.Contains(sessionId, StringComparer.Ordinal)
                || !response.Headers.TryGetValues("X-SwiftAid-Response-Sequence", out var sequences)
                || !long.TryParse(sequences.FirstOrDefault(), out var actualSequence)
                || actualSequence != expectedSequence
                || (response.Content.Headers.ContentLength is > MaximumPcmBytes)
                || (!string.IsNullOrWhiteSpace(expectedPlaybackId)
                    && (!response.Headers.TryGetValues("X-SwiftAid-Playback-Id", out var playbackIds)
                        || !playbackIds.Contains(expectedPlaybackId, StringComparer.Ordinal)))
                || !response.Headers.TryGetValues("X-SwiftAid-Sample-Format", out var formats)
                || !formats.Contains("s16le", StringComparer.Ordinal)
                || !response.Headers.TryGetValues("X-SwiftAid-Channels", out var channels)
                || !channels.Contains("1", StringComparer.Ordinal)
                || !response.Headers.TryGetValues("X-SwiftAid-Voice-Id", out var voices)
                || !response.Headers.TryGetValues("X-SwiftAid-Sample-Rate", out var sampleRates)
                || !int.TryParse(sampleRates.FirstOrDefault(), out var sampleRate)
                || sampleRate is < 8_000 or > 48_000)
            {
                return null;
            }

            var pcm = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = new EllaiResponseAudioStream(
                pcm, sampleRate, voices.First(), expectedSequence, response);
            response = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            response?.Dispose();
        }
    }

    public async Task<bool> InterruptResponseAudioAsync(
        string sessionId,
        long sequence,
        string playbackId,
        CancellationToken cancellationToken,
        string? turnId = null,
        long streamEpoch = 0)
    {
        if (sequence < 1 || !Guid.TryParseExact(playbackId, "N", out _))
        {
            return false;
        }

        try
        {
            var route = $"{SessionRoute(sessionId)}/responses/{sequence}/audio/{Uri.EscapeDataString(playbackId)}/interrupt";
            if (!string.IsNullOrWhiteSpace(turnId) && streamEpoch > 0)
            {
                route += $"?turnId={Uri.EscapeDataString(turnId)}&streamEpoch={streamEpoch}";
            }

            using var response = await PostAsJsonAsync(
                route,
                new { }, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<EllaiResponseAudio?> DownloadListeningGreetingAudioAsync(
        string sessionId, CancellationToken cancellationToken) =>
        DownloadPcmAudioAsync(
            sessionId, $"{SessionRoute(sessionId)}/voice/greeting/audio", 0,
            "live-activation", expectedPlaybackId: null, cancellationToken);

    private async Task<EllaiResponseAudio?> DownloadPcmAudioAsync(
        string sessionId,
        string requestUri,
        long expectedSequence,
        string? expectedPurpose,
        string? expectedPlaybackId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType != "application/vnd.swiftaid.pcm"
                || !response.Headers.TryGetValues("X-SwiftAid-Session-Id", out var sessionIds)
                || !sessionIds.Contains(sessionId, StringComparer.Ordinal)
                || !response.Headers.TryGetValues("X-SwiftAid-Response-Sequence", out var sequences)
                || !long.TryParse(sequences.FirstOrDefault(), out var actualSequence)
                || actualSequence != expectedSequence
                || (!string.IsNullOrWhiteSpace(expectedPlaybackId)
                    && (!response.Headers.TryGetValues("X-SwiftAid-Playback-Id", out var playbackIds)
                        || !playbackIds.Contains(expectedPlaybackId, StringComparer.Ordinal)))
                || (expectedPurpose is not null
                    && (!response.Headers.TryGetValues("X-SwiftAid-Speech-Purpose", out var purposes)
                        || !purposes.Contains(expectedPurpose, StringComparer.Ordinal)))
                || !response.Headers.TryGetValues("X-SwiftAid-Sample-Format", out var formats)
                || !formats.Contains("s16le", StringComparer.Ordinal)
                || !response.Headers.TryGetValues("X-SwiftAid-Channels", out var channels)
                || !channels.Contains("1", StringComparer.Ordinal)
                || !response.Headers.TryGetValues("X-SwiftAid-Voice-Id", out var voices)
                || !response.Headers.TryGetValues("X-SwiftAid-Sample-Rate", out var sampleRates)
                || !int.TryParse(sampleRates.FirstOrDefault(), out var sampleRate)
                || sampleRate is < 8_000 or > 48_000)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[32 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaximumPcmBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (buffer.Length == 0 || (buffer.Length & 1) != 0)
            {
                return null;
            }

            return new EllaiResponseAudio(buffer.ToArray(), sampleRate, voices.First(), expectedSequence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<EllaiReviewDto?> GetActiveReviewAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        if (_useGuestRoutes)
        {
            return null;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        try
        {
            using var response = await _http.GetAsync(
                $"api/v1/ellai/sessions/{Uri.EscapeDataString(sessionId)}/approval-reviews/active",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<EllaiReviewDto>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(
        string sessionId, string requestId, IReadOnlyList<string> actionIds,
        CancellationToken cancellationToken)
    {
        if (_useGuestRoutes)
        {
            return null;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"api/v1/ellai/sessions/{Uri.EscapeDataString(sessionId)}/approval-reviews/{Uri.EscapeDataString(requestId)}/approve",
                new { sessionId, actionIds }, Json, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<IReadOnlyList<EllaiApprovedReceiptDto>>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> RejectReviewAsync(
        string sessionId, string requestId, CancellationToken cancellationToken)
    {
        if (_useGuestRoutes)
        {
            return false;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"api/v1/ellai/sessions/{Uri.EscapeDataString(sessionId)}/approval-reviews/{Uri.EscapeDataString(requestId)}/reject",
                new { sessionId }, Json, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> ReportUiObservationAsync(
        string sessionId, UiObservationReport report, CancellationToken cancellationToken)
    {
        if (_useGuestRoutes)
        {
            return false;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"api/v1/ellai/sessions/{Uri.EscapeDataString(sessionId)}/ui-observations",
                new { sessionId, report.ViewInstanceId, report.ContextStamp, report.Platform, report.MountedControlIds },
                Json, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ListReceiptsAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        if (_useGuestRoutes)
        {
            return null;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        try
        {
            using var response = await _http.GetAsync(
                $"api/v1/ellai/sessions/{Uri.EscapeDataString(sessionId)}/receipts",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<IReadOnlyList<EllaiApprovedReceiptDto>>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string SessionRoute(string sessionId) =>
        $"api/v1/ellai/{(_useGuestRoutes ? "guest/" : string.Empty)}sessions/{Uri.EscapeDataString(sessionId)}";

    private async Task<HttpResponseMessage> PostBytesAsync(
        string requestUri, byte[] bytes, string contentType, CancellationToken cancellationToken)
    {
        if (!_useGuestRoutes)
        {
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return await _http.PostAsync(requestUri, content, cancellationToken).ConfigureAwait(false);
        }

        var token = await GetGuestAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Guest request validation is unavailable.");
        }

        var response = await SendGuestBytesAsync(requestUri, bytes, contentType, token, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.BadRequest
            || !await IsAntiforgeryRejectionAsync(response, cancellationToken).ConfigureAwait(false))
        {
            return response;
        }

        response.Dispose();
        _guestAntiforgeryToken = null;
        token = await GetGuestAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(token)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            : await SendGuestBytesAsync(requestUri, bytes, contentType, token, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendGuestBytesAsync(
        string requestUri, byte[] bytes, string contentType, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostAsJsonAsync<T>(
        string requestUri, T value, CancellationToken cancellationToken)
    {
        if (!_useGuestRoutes)
        {
            return await _http.PostAsJsonAsync(requestUri, value, Json, cancellationToken).ConfigureAwait(false);
        }

        var requestToken = await GetGuestAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestToken))
        {
            throw new InvalidOperationException("Guest request validation is unavailable.");
        }

        var response = await SendGuestPostAsync(requestUri, value, requestToken, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.BadRequest
            || !await IsAntiforgeryRejectionAsync(response, cancellationToken).ConfigureAwait(false))
        {
            return response;
        }

        // The server rejects an invalid token before invoking the route
        // handler and refreshes the antiforgery cookie. Fetch its matching
        // request token and retry this write once; other 400 responses are
        // returned untouched, and repeated failures cannot loop.
        response.Dispose();
        _guestAntiforgeryToken = null;
        requestToken = await GetGuestAntiforgeryTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestToken))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
        }

        return await SendGuestPostAsync(requestUri, value, requestToken, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendGuestPostAsync<T>(
        string requestUri, T value, string requestToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = System.Net.Http.Json.JsonContent.Create(value, options: Json),
        };
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", requestToken);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsAntiforgeryRejectionAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("title", out var title)
                && string.Equals(
                    title.GetString(),
                    "The guest Ellai request could not be validated.",
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<string?> GetGuestAntiforgeryTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_guestAntiforgeryToken))
        {
            return _guestAntiforgeryToken;
        }

        using var response = await _http.GetAsync(
            "api/v1/ellai/guest/antiforgery", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<EllaiGuestAntiforgeryToken>(Json, cancellationToken)
            .ConfigureAwait(false);
        _guestAntiforgeryToken = payload?.RequestToken;
        return _guestAntiforgeryToken;
    }

    private sealed record EllaiGuestAntiforgeryToken(string RequestToken);
}

/// <summary>Client-side subscription over the session event journal. Tracks
/// one session at a time with a monotonic cursor: duplicate deliveries of
/// the same review focus it (never stack dialogs), and a session change or
/// cancellation fences stale events. The host opens the approval dialog on
/// <see cref="ApprovalRequired"/> and closes it on
/// <see cref="SessionCancelled"/>.</summary>
public sealed class EllaiEventPoller(IEllaiReviewClient reviews)
{
    private readonly IEllaiReviewClient _reviews =
        reviews ?? throw new ArgumentNullException(nameof(reviews));

    private readonly HashSet<string> _seenReviews = new(StringComparer.Ordinal);

    public event Action<string>? ApprovalRequired;

    public event Action? SessionCancelled;

    /// <summary>Trusted runtime progress for the composer status line:
    /// deterministic template phase and text, never model output.</summary>
    public event Action<string, string>? ActivityUpdated;

    public event Action<EllaiSessionResponseDto>? ResponseReceived;
    public event Action<IReadOnlyList<EllaiConversationTurnDto>>? ConversationUpdated;
    public event Action? ResponseStreamReset;
    private string? _streamId;

    public string? SessionId { get; private set; }

    public long Cursor { get; private set; }

    public long ResponseCursor { get; private set; }

    public void TrackSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!string.Equals(SessionId, sessionId, StringComparison.Ordinal))
        {
            SessionId = sessionId;
            Cursor = 0;
            ResponseCursor = 0;
            _streamId = null;
            _seenReviews.Clear();
        }
    }

    public void ForgetSession()
    {
        SessionId = null;
        Cursor = 0;
        ResponseCursor = 0;
        _streamId = null;
        _seenReviews.Clear();
    }

    public async Task PollOnceAsync(int waitSeconds, CancellationToken cancellationToken)
    {
        if (SessionId is null)
        {
            return;
        }

        var sessionId = SessionId;
        var page = await _reviews
            .PollEventsAsync(sessionId, Cursor, waitSeconds, cancellationToken)
            .ConfigureAwait(false);
        if (SessionId != sessionId) return;
        if (page?.Events is not null)
        {
            foreach (var @event in page.Events)
            {
                if (@event.Sequence <= Cursor)
                {
                    continue;
                }

                if (string.Equals(@event.Kind, "approval-required", StringComparison.Ordinal)
                    && @event.Ref is not null
                    && _seenReviews.Add(@event.Ref))
                {
                    ApprovalRequired?.Invoke(@event.Ref);
                }
                else if (string.Equals(@event.Kind, "session-cancelled", StringComparison.Ordinal))
                {
                    SessionCancelled?.Invoke();
                }
                else if (string.Equals(@event.Kind, "activity", StringComparison.Ordinal))
                {
                    ActivityUpdated?.Invoke(@event.Ref ?? string.Empty, @event.Detail);
                }
                Cursor = @event.Sequence;
            }
        }

        var responses = await _reviews.PollResponsesAsync(
            sessionId, ResponseCursor, cancellationToken).ConfigureAwait(false);
        if (SessionId != sessionId) return;
        if (responses?.Responses is null)
        {
            return;
        }

        if (responses.StreamId is not null && responses.StreamId != _streamId)
        {
            if (_streamId is not null)
            {
                Cursor = 0;
                ResponseCursor = 0;
                ResponseStreamReset?.Invoke();
            }
            _streamId = responses.StreamId;
        }
        // Establish the durable user-turn identity before its response chunks.
        // Voice admission no longer waits for the separate voice-status poll.
        if (responses.Conversation is not null) ConversationUpdated?.Invoke(responses.Conversation);
        foreach (var response in responses.Responses)
        {
            if (response.Sequence <= ResponseCursor
                || !string.Equals(response.SessionId, SessionId, StringComparison.Ordinal))
            {
                continue;
            }

            ResponseReceived?.Invoke(response);
            ResponseCursor = response.Sequence;
        }
    }
}
