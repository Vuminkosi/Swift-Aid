using System.Collections.Concurrent;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Mobile.Services.Authentication;
using SwiftAid.UI.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Mobile.Services.Ellai;

/// <summary>
/// Selects the Gateway's authenticated or guest Ellai contract for each
/// session. Guest sessions remain pinned to the antiforgery-protected guest
/// routes for their lifetime, even if account state changes afterward.
/// </summary>
public sealed class MobileEllaiReviewClient : IEllaiReviewClient
{
    private readonly EllaiReviewHttpClient? _authenticated;
    private readonly EllaiReviewHttpClient? _guest;
    private readonly IMobileAuthenticationSession _authentication;
    private readonly ConcurrentDictionary<string, byte> _guestSessions = new(StringComparer.Ordinal);

    public bool UsesGatewayPcmAudioDownload => true;

    public async Task<EllaiSessionHistoryPageDto?> ListSessionHistoryAsync(int offset, CancellationToken cancellationToken)
    {
        if (_authenticated is null || _guest is null) return null;
        try
        {
            var signedIn = await _authentication.IsSignedInAsync(cancellationToken).ConfigureAwait(false);
            var page = await (signedIn ? _authenticated : _guest).ListSessionHistoryAsync(offset, cancellationToken).ConfigureAwait(false);
            if (!signedIn && page is not null)
                foreach (var session in page.Sessions) _guestSessions.TryAdd(session.SessionId, 0);
            return page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    public MobileEllaiReviewClient(
        HttpClient? authenticatedHttp,
        HttpClient? guestHttp,
        IMobileAuthenticationSession authentication)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        if (authenticatedHttp is not null && guestHttp is not null)
        {
            _authenticated = new EllaiReviewHttpClient(authenticatedHttp, useLiveVoiceWebSocket: true);
            _guest = new EllaiReviewHttpClient(guestHttp, useGuestRoutes: true, useLiveVoiceWebSocket: true);
        }
    }

    public async Task<EllaiSessionStartResult> StartSessionAsync(
        EllaiMode mode, CancellationToken cancellationToken)
    {
        if (_authenticated is null || _guest is null)
        {
            return new(EllaiClientOperationStatus.Unavailable);
        }

        bool isSignedIn;
        try
        {
            isSignedIn = await _authentication.IsSignedInAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // An unreadable authentication state must not silently downgrade
            // an account launch into a separate guest identity.
            return new(EllaiClientOperationStatus.Unavailable);
        }
        var client = isSignedIn ? _authenticated : _guest;
        var result = await client.StartSessionAsync(mode, cancellationToken).ConfigureAwait(false);
        if (!isSignedIn && result.Status == EllaiClientOperationStatus.Accepted
            && result.Session is { SessionId.Length: > 0 } session)
        {
            _guestSessions.TryAdd(session.SessionId, 0);
        }

        return result;
    }

    public Task<EllaiUtteranceSubmitResult> SubmitUtteranceAsync(
        SubmitUtteranceRequest request, CancellationToken cancellationToken) =>
        ClientFor(request.SessionId).SubmitUtteranceAsync(request, cancellationToken);

    public async Task<EllaiSessionStartResult> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_authenticated is null || _guest is null) return new(EllaiClientOperationStatus.Unavailable);
        if (_guestSessions.ContainsKey(sessionId))
            return await _guest.ResumeSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            var signedIn = await _authentication.IsSignedInAsync(cancellationToken).ConfigureAwait(false);
            var result = await (signedIn ? _authenticated : _guest).ResumeSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!signedIn && result.Status == EllaiClientOperationStatus.Accepted)
                _guestSessions.TryAdd(sessionId, 0);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(EllaiClientOperationStatus.OutcomeUnknown); }
    }

    public Task<bool> GrantPublicProductHelpConsentAsync(
        string sessionId, CancellationToken cancellationToken) =>
        ClientFor(sessionId).GrantPublicProductHelpConsentAsync(sessionId, cancellationToken);

    public Task<bool> GrantVoiceCaptureConsentAsync(string sessionId, CancellationToken cancellationToken) =>
        ClientFor(sessionId).GrantVoiceCaptureConsentAsync(sessionId, cancellationToken);

    public Task<EllaiVoiceTranscriptionResult?> TranscribeVoiceAsync(
        string sessionId, byte[] audio, EllaiVoiceCaptureMode mode, CancellationToken cancellationToken,
        string? voiceCaptureId = null, long voiceCaptureOffsetSamples = 0) =>
        ClientFor(sessionId).TranscribeVoiceAsync(
            sessionId, audio, mode, cancellationToken, voiceCaptureId, voiceCaptureOffsetSamples);

    public Task<EllaiVoiceTranscriptionResult?> TranscribeLiveVoiceStreamAsync(
        string sessionId, Stream audio, CancellationToken cancellationToken, string? voiceCaptureId = null,
        long voiceCaptureOffsetSamples = 0) =>
        ClientFor(sessionId).TranscribeLiveVoiceStreamAsync(
            sessionId, audio, cancellationToken, voiceCaptureId, voiceCaptureOffsetSamples);

    public Task<EllaiVoiceTurnStatusDto?> GetVoiceTurnStatusAsync(
        string sessionId, CancellationToken cancellationToken, long afterSequence = 0) =>
        ClientFor(sessionId).GetVoiceTurnStatusAsync(sessionId, cancellationToken, afterSequence);

    public async Task<EllaiSessionCancelResult> CancelSessionAsync(
        string sessionId, long expectedSessionVersion, CancellationToken cancellationToken)
    {
        var result = await ClientFor(sessionId)
            .CancelSessionAsync(sessionId, expectedSessionVersion, cancellationToken).ConfigureAwait(false);
        if (result.Status == EllaiClientOperationStatus.Cancelled)
        {
            _guestSessions.TryRemove(sessionId, out _);
        }

        return result;
    }

    public Task<EllaiSessionEventPageDto?> PollEventsAsync(
        string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken) =>
        ClientFor(sessionId).PollEventsAsync(sessionId, cursor, waitSeconds, cancellationToken);

    public Task<EllaiSessionResponsePageDto?> PollResponsesAsync(
        string sessionId, long cursor, CancellationToken cancellationToken) =>
        ClientFor(sessionId).PollResponsesAsync(sessionId, cursor, cancellationToken);

    public Task<EllaiResponseAudio?> DownloadResponseAudioAsync(
        string sessionId, long sequence, CancellationToken cancellationToken) =>
        ClientFor(sessionId).DownloadResponseAudioAsync(sessionId, sequence, cancellationToken);

    public Task<EllaiResponseAudio?> DownloadResponseAudioAsync(
        string sessionId, long sequence, string playbackId, CancellationToken cancellationToken) =>
        ClientFor(sessionId).DownloadResponseAudioAsync(sessionId, sequence, playbackId, cancellationToken);

    public Task<EllaiResponseAudioStream?> DownloadResponseAudioStreamAsync(
        string sessionId, long sequence, string playbackId, CancellationToken cancellationToken) =>
        ClientFor(sessionId).DownloadResponseAudioStreamAsync(
            sessionId, sequence, playbackId, cancellationToken);

    public Task<bool> InterruptResponseAudioAsync(
        string sessionId,
        long sequence,
        string playbackId,
        CancellationToken cancellationToken,
        string? turnId = null,
        long streamEpoch = 0) =>
        ClientFor(sessionId).InterruptResponseAudioAsync(
            sessionId, sequence, playbackId, cancellationToken, turnId, streamEpoch);

    public Task<EllaiResponseAudio?> DownloadListeningGreetingAudioAsync(
        string sessionId, CancellationToken cancellationToken) =>
        ClientFor(sessionId).DownloadListeningGreetingAudioAsync(sessionId, cancellationToken);

    public Task<EllaiReviewDto?> GetActiveReviewAsync(
        string sessionId, CancellationToken cancellationToken) =>
        ClientFor(sessionId).GetActiveReviewAsync(sessionId, cancellationToken);

    public Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(
        string sessionId, string requestId, IReadOnlyList<string> actionIds,
        CancellationToken cancellationToken) =>
        ClientFor(sessionId).ApproveSelectedAsync(sessionId, requestId, actionIds, cancellationToken);

    public Task<bool> RejectReviewAsync(
        string sessionId, string requestId, CancellationToken cancellationToken) =>
        ClientFor(sessionId).RejectReviewAsync(sessionId, requestId, cancellationToken);

    public Task<bool> ReportUiObservationAsync(
        string sessionId, UiObservationReport report, CancellationToken cancellationToken) =>
        ClientFor(sessionId).ReportUiObservationAsync(sessionId, report, cancellationToken);

    public Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ListReceiptsAsync(
        string sessionId, CancellationToken cancellationToken) =>
        ClientFor(sessionId).ListReceiptsAsync(sessionId, cancellationToken);

    private IEllaiReviewClient ClientFor(string? sessionId)
    {
        if (_authenticated is null || _guest is null)
        {
            return UnavailableReviewClient.Instance;
        }

        return sessionId is not null && _guestSessions.ContainsKey(sessionId)
            ? _guest
            : _authenticated;
    }

    private sealed class UnavailableReviewClient : IEllaiReviewClient
    {
        public static UnavailableReviewClient Instance { get; } = new();

        public Task<EllaiSessionStartResult> StartSessionAsync(EllaiMode mode, CancellationToken cancellationToken) =>
            Task.FromResult(new EllaiSessionStartResult(EllaiClientOperationStatus.Unavailable));

        public Task<EllaiUtteranceSubmitResult> SubmitUtteranceAsync(SubmitUtteranceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Unavailable, null));

        public Task<bool> GrantPublicProductHelpConsentAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> GrantVoiceCaptureConsentAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<EllaiVoiceTranscriptionResult?> TranscribeVoiceAsync(
            string sessionId, byte[] audio, EllaiVoiceCaptureMode mode, CancellationToken cancellationToken,
            string? voiceCaptureId = null, long voiceCaptureOffsetSamples = 0) =>
            Task.FromResult<EllaiVoiceTranscriptionResult?>(null);

        public Task<EllaiVoiceTranscriptionResult?> TranscribeLiveVoiceStreamAsync(
            string sessionId, Stream audio, CancellationToken cancellationToken, string? voiceCaptureId = null,
            long voiceCaptureOffsetSamples = 0) =>
            Task.FromResult<EllaiVoiceTranscriptionResult?>(null);

        public Task<EllaiSessionCancelResult> CancelSessionAsync(string sessionId, long expectedSessionVersion, CancellationToken cancellationToken) =>
            Task.FromResult(new EllaiSessionCancelResult(EllaiClientOperationStatus.Unavailable));

        public Task<EllaiSessionEventPageDto?> PollEventsAsync(string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken) =>
            Task.FromResult<EllaiSessionEventPageDto?>(null);

        public Task<EllaiReviewDto?> GetActiveReviewAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<EllaiReviewDto?>(null);

        public Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(string sessionId, string requestId, IReadOnlyList<string> actionIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EllaiApprovedReceiptDto>?>(null);

        public Task<bool> RejectReviewAsync(string sessionId, string requestId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> ReportUiObservationAsync(string sessionId, UiObservationReport report, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ListReceiptsAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EllaiApprovedReceiptDto>?>(null);
    }
}

/// <summary>Dynamic guidance UI intents are not yet transported by the
/// mobile host. This adapter fails closed instead of fabricating completion.</summary>
public sealed class UnavailableMobileEllaiGuidanceControl : IEllaiGuidanceControl
{
    public Task<GuidanceSnapshot> StartAsync(
        string journeyId, string journeyVersion, GuidanceMode mode,
        string? startStepId, CancellationToken cancellationToken,
        string? clientCommandId = null) =>
        Task.FromException<GuidanceSnapshot>(new NotSupportedException(
            "Dynamic Ellai guidance control is not available in this mobile build."));

    public Task<GuidanceSnapshot?> DispatchAsync(
        string attemptId, long expectedVersion, GuidanceCommandKind command,
        GuidanceMode mode, string? targetStepId, GuidanceMode? targetMode,
        CancellationToken cancellationToken, string? clientCommandId = null) =>
        Task.FromResult<GuidanceSnapshot?>(null);

    public Task<GuidanceSnapshot?> StopAsync(
        string attemptId, long expectedVersion, GuidanceMode mode,
        CancellationToken cancellationToken, string? clientCommandId = null) =>
        Task.FromResult<GuidanceSnapshot?>(null);
}
