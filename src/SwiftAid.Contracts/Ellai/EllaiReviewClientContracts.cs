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

public sealed record EllaiVoiceTranscriptionResult(string Transcript);

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
    DateTimeOffset CreatedAtUtc);

public sealed record EllaiSessionResponsePageDto(
    IReadOnlyList<EllaiSessionResponseDto> Responses,
    long Cursor);

/// <summary>Bounded little-endian, mono PCM response streamed from the Gateway.</summary>
public sealed record EllaiResponseAudio(byte[] Pcm, int SampleRate, string VoiceId, long Sequence);

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
    bool UsesGatewayPcmAudioDownload => false;

    /// <summary>Starts a durable session for the requested mode. A missing
    /// view is classified so confirmed denials are not presented as ambiguous
    /// remote outcomes; callers never invent a session id.</summary>
    Task<EllaiSessionStartResult> StartSessionAsync(EllaiMode mode, CancellationToken cancellationToken);

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
        string sessionId, byte[] audio, EllaiVoiceCaptureMode mode, CancellationToken cancellationToken) =>
        Task.FromResult<EllaiVoiceTranscriptionResult?>(null);

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
