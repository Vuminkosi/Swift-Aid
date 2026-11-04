namespace SwiftAid.Client.Intake;

/// <summary>Durable, encrypted local cleanup intent; it contains only opaque record identifiers.</summary>
public sealed record GuestAudioCleanupIntent(
    string IntentKey,
    Guid SupersededSubmissionId,
    long ExpectedSupersededVersion,
    Guid ReplacementSubmissionId,
    long ExpectedReplacementVersion,
    Guid AttachmentId,
    DateTimeOffset CreatedAtUtc)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(IntentKey) && IntentKey.Length <= 220 &&
        SupersededSubmissionId != Guid.Empty && ExpectedSupersededVersion > 0 &&
        ReplacementSubmissionId != Guid.Empty && ExpectedReplacementVersion > 0 &&
        SupersededSubmissionId != ReplacementSubmissionId && AttachmentId != Guid.Empty &&
        CreatedAtUtc.Offset == TimeSpan.Zero;
}

public enum GuestAudioCleanupJournalCode
{
    Recorded,
    AlreadyPending,
    Completed,
    AlreadyCompleted,
    Cancelled,
    AlreadyCancelled,
    InvalidRequest,
    Unavailable,
    Failed
}

public sealed record GuestAudioCleanupJournalResult(
    GuestAudioCleanupJournalCode Code,
    string? Message = null)
{
    public bool IsPending => Code is GuestAudioCleanupJournalCode.Recorded or GuestAudioCleanupJournalCode.AlreadyPending;

    public bool IsComplete => Code is GuestAudioCleanupJournalCode.Completed or GuestAudioCleanupJournalCode.AlreadyCompleted;
}

/// <summary>Encrypted local journal for restart-safe removal of superseded guest audio.</summary>
public interface IGuestAudioCleanupJournal
{
    Task<GuestAudioCleanupJournalResult> EnsurePendingAsync(
        GuestAudioCleanupIntent intent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GuestAudioCleanupIntent>> GetPendingAsync(
        CancellationToken cancellationToken = default);

    Task<GuestAudioCleanupJournalResult> CompleteAsync(
        string intentKey,
        CancellationToken cancellationToken = default);

    Task<GuestAudioCleanupJournalResult> CancelAsync(
        string intentKey,
        CancellationToken cancellationToken = default);
}

public enum GuestAudioCleanupCode
{
    Removed,
    AlreadyAbsent,
    RetainedForHistory,
    NotSafe,
    InvalidRequest,
    Unavailable,
    Failed
}

public sealed record GuestAudioCleanupResult(
    GuestAudioCleanupCode Code,
    string? Message = null)
{
    public bool VerifiedAbsent => Code is GuestAudioCleanupCode.Removed or GuestAudioCleanupCode.AlreadyAbsent;

    public bool IsTerminal => VerifiedAbsent || Code == GuestAudioCleanupCode.RetainedForHistory;
}

public enum GuestAudioRetakeStatus
{
    Replaced,
    CleanupPending,
    Conflict,
    NotFound,
    NotEditable,
    InvalidRequest,
    Unavailable,
    Failed,
    Cancelled
}

/// <summary>Result of creating a new draft revision without one explicitly selected saved recording.</summary>
public sealed record GuestAudioRetakeResult(
    GuestAudioRetakeStatus Status,
    GuestSubmissionSummary? Replacement,
    Guid? SupersededSubmissionId,
    Guid? ReplacementSubmissionId,
    Guid? AttachmentId,
    string? CleanupIntentKey,
    string? Message)
{
    public bool RevisionCreated => Replacement is not null;

    public bool CleanupPending => Status == GuestAudioRetakeStatus.CleanupPending;
}

public sealed record GuestAudioCleanupRetrySummary(
    int Completed,
    int Pending,
    int Cancelled,
    int Failed,
    int RetainedForHistory = 0);
