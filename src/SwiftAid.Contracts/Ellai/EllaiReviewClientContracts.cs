namespace SwiftAid.Contracts.Ellai;

public static class EllaiConsentPurposes
{
    public const string PublicProductHelp = "ellai-public-product-help";
    public const string VoiceTranscription = "ellai-voice-transcription";
}

public enum EllaiVoiceCaptureMode
{
    Recorded,
    Live,
}

public sealed record EllaiVoiceTranscriptionResult(
    string Transcript, string? ClientUtteranceId = null,
    EllaiUtteranceSubmitResult? Admission = null, bool ServerSubmission = false);

/// <summary>Transient state for the active voice stream, scoped to the owning
/// Ellai session. Completed transcripts are returned only to that session owner.</summary>
public sealed record EllaiVoiceTurnStatusDto(
    string State,
    long Epoch,
    int ConfirmedSpeechMilliseconds,
    int SilenceMilliseconds,
    string? PartialTranscript,
    IReadOnlyList<EllaiCompletedVoiceTurnDto>? CompletedTurns = null,
    string? SessionId = null);

/// <summary>A provider-final transcript confirmed by Gateway Silero endpointing.
/// Sequence is monotonic for the parent Ellai session across provider stream reconnects.</summary>
public sealed record EllaiCompletedVoiceTurnDto(
    long Sequence,
    string ProviderTurnId,
    string Transcript,
    string? SessionId = null,
    string? TurnId = null,
    long StreamEpoch = 0,
    string Kind = "TurnCompleted",
    long AudioStartSample = -1,
    long AudioEndSample = -1,
    string? VoiceCaptureId = null,
    bool ServerSubmission = false,
    EllaiUtteranceSubmitResult? Admission = null);

/// <summary>Session UI event as delivered by the review event transport.</summary>
public sealed record EllaiSessionEventDto(
    long Sequence,
    string SessionId,
    string Kind,
    string? Ref,
    string Detail,
    DateTimeOffset AtUtc);

/// <summary>Bounded poll page with the cursor the client passes back.</summary>
public sealed record EllaiSessionEventPageDto(
    IReadOnlyList<EllaiSessionEventDto> Events,
    long Cursor);

/// <summary>A current-turn response retained only for the active Ellai
/// session. It is separate from progress/events so model text is never
/// misrepresented as activity or approval evidence.</summary>
public sealed record EllaiSessionResponseDto(
    long Sequence,
    string SessionId,
    string Text,
    DateTimeOffset CreatedAtUtc,
    string? ClientUtteranceId = null,
    bool IsFinal = true,
    bool IsReconciliation = false,
    string? Outcome = null,
    EllaiInteractionProgressDto? Progress = null,
    string Language = "eng");

/// <summary>Ephemeral runtime narration; never an assistant answer or approval receipt.</summary>
public sealed record EllaiInteractionProgressDto(
    string EventId, string State, string Operation, bool Speak,
    long ControllerEpoch, DateTimeOffset ExpiresAtUtc, string Language = "en");

public sealed record EllaiSessionResponsePageDto(
    IReadOnlyList<EllaiSessionResponseDto> Responses,
    long Cursor,
    string? StreamId = null,
    IReadOnlyList<EllaiConversationTurnDto>? Conversation = null);

/// <summary>Owner-authorized durable turn. A provisional chunk is never a completed turn.</summary>
public sealed record EllaiConversationTurnDto(
    string ClientUtteranceId, long Sequence, string? UserText, string? AssistantText,
    string Outcome, int AttemptCount, DateTimeOffset? CompletedAtUtc)
{
    /// <summary>Unexecuted model proposals. Consumers must request fresh authorization and plan validation.
    /// Restoring conversation history must never automatically execute these proposals.</summary>
    public IReadOnlyList<EllaiUiProposalDto> UiProposals { get; init; } = [];
}

public sealed record EllaiUiProposalDto(string ProposalId, UiIntentProposal Proposal);

public sealed record EllaiSessionHistoryItemDto(
    string SessionId, string Mode, string Status, DateTimeOffset UpdatedAtUtc,
    string? Preview = null, int? TurnCount = null);

public sealed record EllaiSessionHistoryPageDto(
    IReadOnlyList<EllaiSessionHistoryItemDto> Sessions, int? NextOffset);

/// <summary>Bounded little-endian, mono PCM response streamed from the Gateway.</summary>
public sealed record EllaiResponseAudio(byte[] Pcm, int SampleRate, string VoiceId, long Sequence);

/// <summary>Transient Gateway PCM response that keeps the transport alive until
/// playback finishes or is canceled. Disposing it closes both the PCM stream
/// and its owning response.</summary>
public sealed class EllaiResponseAudioStream : IAsyncDisposable
{
    private readonly IDisposable? _owner;
    private int _disposed;

    public EllaiResponseAudioStream(
        Stream pcm, int sampleRate, string voiceId, long sequence, IDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
        if (!pcm.CanRead || sampleRate is < 8_000 or > 48_000 || sequence < 0)
        {
            throw new ArgumentException("The Gateway PCM stream metadata is invalid.");
        }

        Pcm = pcm;
        SampleRate = sampleRate;
        VoiceId = voiceId;
        Sequence = sequence;
        _owner = owner;
    }

    public Stream Pcm { get; }

    public int SampleRate { get; }

    public string VoiceId { get; }

    public long Sequence { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Pcm.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _owner?.Dispose();
        }
    }
}

/// <summary>Open review for dialog rendering: identity, binding, state and
/// safe display rows. Approval still resolves server-side.</summary>
public sealed record EllaiReviewDto(
    string RequestId,
    string SessionId,
    string PlanHash,
    int PlanVersion,
    string State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<EllaiApprovalActionDescriptor> Actions);

/// <summary>Approved receipt identity for the dialog confirmation path.</summary>
public sealed record EllaiApprovedReceiptDto(
    string ReceiptId,
    string ActionId,
    string Status);

public enum EllaiClientOperationStatus
{
    Accepted,
    Cancelled,
    Unavailable,
    Unauthorized,
    InvalidRequest,
    Conflict,
    PolicyBlocked,
    OutcomeUnknown,
}

public sealed record EllaiSessionStartResult(
    EllaiClientOperationStatus Status,
    EllaiSessionView? Session = null);

public sealed record EllaiUtteranceSubmitResult(
    EllaiClientOperationStatus Status,
    EllaiSessionView? Session);

public sealed record EllaiSessionCancelResult(
    EllaiClientOperationStatus Status,
    EllaiSessionView? Session = null);

/// <summary>Review client port shared by the in-process gateway adapter and
/// the HTTP transport. Null means unavailable or inapplicable: the caller
/// keeps its cursor and retries; replay never replays commands.</summary>
public interface IEllaiReviewClient
{
    Task<EllaiGuidanceAdmissionDto?> ReadGuidanceAdmissionAsync(
        string sessionId, string proposalId, CancellationToken cancellationToken) =>
        Task.FromResult<EllaiGuidanceAdmissionDto?>(null);

    bool UsesGatewayPcmAudioDownload => false;

    Uri CreateLiveVoiceWebSocketUri(Uri baseUri, string sessionId, string voiceCaptureId, long voiceCaptureOffsetSamples = 0)
    {
        var uri = new Uri(baseUri, $"api/v1/ellai/sessions/{Uri.EscapeDataString(sessionId)}/voice/live-stream?voiceCaptureId={Uri.EscapeDataString(voiceCaptureId)}&serverSubmission=true&voiceCaptureOffsetSamples={voiceCaptureOffsetSamples}");
        return new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" }.Uri;
    }

    /// <summary>Starts a durable session for the requested mode. A missing
    /// view is classified so confirmed denials are not presented as ambiguous
    /// remote outcomes; callers never invent a session id.</summary>
    Task<EllaiSessionStartResult> StartSessionAsync(EllaiMode mode, CancellationToken cancellationToken);

    Task<EllaiSessionStartResult> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(new EllaiSessionStartResult(EllaiClientOperationStatus.Unavailable));

    Task<EllaiSessionHistoryPageDto?> ListSessionHistoryAsync(int offset, CancellationToken cancellationToken) =>
        Task.FromResult<EllaiSessionHistoryPageDto?>(null);

    /// <summary>Submits one finalized user utterance through the authenticated
    /// session contract. Null means the request was unavailable, invalid, or
    /// rejected by the current session fence; callers must not report it sent.</summary>
    Task<EllaiUtteranceSubmitResult> SubmitUtteranceAsync(
        SubmitUtteranceRequest request, CancellationToken cancellationToken);

    /// <summary>Records the user's explicit, session-scoped consent to send
    /// classified public SwiftAid product-help questions for reasoning.</summary>
    Task<bool> GrantPublicProductHelpConsentAsync(
        string sessionId, CancellationToken cancellationToken);

    Task<bool> GrantVoiceCaptureConsentAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    Task<EllaiVoiceTranscriptionResult?> TranscribeVoiceAsync(
        string sessionId, byte[] audio, EllaiVoiceCaptureMode mode, CancellationToken cancellationToken,
        string? voiceCaptureId = null, long voiceCaptureOffsetSamples = 0) =>
        Task.FromResult<EllaiVoiceTranscriptionResult?>(null);

    Task<EllaiVoiceTranscriptionResult?> TranscribeLiveVoiceStreamAsync(
        string sessionId, Stream audio, CancellationToken cancellationToken, string? voiceCaptureId = null,
        long voiceCaptureOffsetSamples = 0) =>
        Task.FromResult<EllaiVoiceTranscriptionResult?>(null);

    Task<EllaiVoiceTurnStatusDto?> GetVoiceTurnStatusAsync(
        string sessionId, CancellationToken cancellationToken, long afterSequence = 0) =>
        Task.FromResult<EllaiVoiceTurnStatusDto?>(null);

    Task<EllaiSessionCancelResult> CancelSessionAsync(
        string sessionId, long expectedSessionVersion, CancellationToken cancellationToken);

    Task<EllaiSessionEventPageDto?> PollEventsAsync(
        string sessionId, long cursor, int waitSeconds, CancellationToken cancellationToken);

    Task<EllaiSessionResponsePageDto?> PollResponsesAsync(
        string sessionId, long cursor, CancellationToken cancellationToken) =>
        Task.FromResult<EllaiSessionResponsePageDto?>(null);

    Task<EllaiResponseAudio?> DownloadResponseAudioAsync(
        string sessionId, long sequence, CancellationToken cancellationToken) =>
        Task.FromResult<EllaiResponseAudio?>(null);

    Task<EllaiResponseAudio?> DownloadResponseAudioAsync(
        string sessionId, long sequence, string playbackId, CancellationToken cancellationToken) =>
        DownloadResponseAudioAsync(sessionId, sequence, cancellationToken);

    async Task<EllaiResponseAudioStream?> DownloadResponseAudioStreamAsync(
        string sessionId, long sequence, string playbackId, CancellationToken cancellationToken)
    {
        var audio = await DownloadResponseAudioAsync(
            sessionId, sequence, playbackId, cancellationToken).ConfigureAwait(false);
        return audio is null
            ? null
            : new EllaiResponseAudioStream(
                new MemoryStream(audio.Pcm, writable: false),
                audio.SampleRate,
                audio.VoiceId,
                audio.Sequence);
    }

    Task<bool> InterruptResponseAudioAsync(
        string sessionId,
        long sequence,
        string playbackId,
        CancellationToken cancellationToken,
        string? turnId = null,
        long streamEpoch = 0) =>
        Task.FromResult(false);

    Task<EllaiResponseAudio?> DownloadListeningGreetingAudioAsync(
        string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<EllaiResponseAudio?>(null);

    Task<EllaiReviewDto?> GetActiveReviewAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Posts the client's mounted-control report for the live UI
    /// observation channel. False means unavailable: the caller keeps its
    /// last report and retries; the server never invents observations.</summary>
    Task<bool> ReportUiObservationAsync(
        string sessionId, UiObservationReport report, CancellationToken cancellationToken);

    /// <summary>Re-reads the session's durable receipts for post-approval
    /// reconciliation (RT-60). Null means unavailable; an empty list is an
    /// authorized empty, never proof that an effect did not occur.</summary>
    Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ListReceiptsAsync(
        string sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(
        string sessionId, string requestId, IReadOnlyList<string> actionIds,
        CancellationToken cancellationToken);

    Task<bool> RejectReviewAsync(
        string sessionId, string requestId, CancellationToken cancellationToken);
}

/// <summary>Readiness for a persisted proposal; never permission to execute an application action.</summary>
public sealed record EllaiGuidanceAdmissionDto(string Status, string? Objective, long InteractionEpoch);
