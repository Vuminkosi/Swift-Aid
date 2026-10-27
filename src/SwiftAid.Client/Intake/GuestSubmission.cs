using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Intake;

public enum GuestSubmissionLocalState
{
    Draft,
    Queued,
    Received,
    NeedsAttention,
    Superseded
}

public enum GuestAudioLocalState
{
    LocalOnly
}

public sealed record GuestAudioAsset(
    Guid AttachmentId,
    string Sha256,
    string MediaType,
    long ByteLength,
    int DurationMilliseconds,
    GuestAudioLocalState State);

/// <summary>Protected local envelope. Receipt proof and capability never belong in UI projections.</summary>
public sealed record GuestSubmissionRecord(
    Guid ClientSubmissionId,
    Guid SessionId,
    IntakeSubmission Submission,
    string ReceiptProof,
    string PayloadSha256,
    GuestSubmissionLocalState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? IntakeCapability = null,
    DateTimeOffset? CapabilityExpiresAtUtc = null,
    IntakeReceipt? Receipt = null,
    IReadOnlyList<GuestAudioAsset>? AudioAssets = null);

/// <summary>Safe view that excludes report text, receipt proof, and intake capability.</summary>
public sealed record GuestSubmissionSummary(
    Guid ClientSubmissionId,
    GuestSubmissionLocalState State,
    DateTimeOffset CreatedAtUtc,
    IntakeReceipt? Receipt);

/// <summary>Editable unsubmitted report details without its receipt proof or session capability.</summary>
public sealed record GuestDraftDetails(
    Guid ClientSubmissionId,
    IntakeSubmission Submission,
    GuestSubmissionLocalState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record GuestSyncSummary(int Queued, int Received, int RetryPending, int NeedsAttention);

public sealed record GuestReceiptRefreshSummary(int Updated, int RetryPending);

public interface IGuestSubmissionPersistence
{
    Task<GuestSubmissionRecord?> GetAsync(Guid clientSubmissionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GuestSubmissionRecord>> GetAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(GuestSubmissionRecord submission, CancellationToken cancellationToken);

    Task SaveBatchAsync(IReadOnlyList<GuestSubmissionRecord> submissions, CancellationToken cancellationToken);
}

public interface IGuestSubmissionStore
{
    Task<GuestSubmissionSummary> CreateDraftAsync(IntakeSubmission submission, CancellationToken cancellationToken = default);

    Task<GuestSubmissionSummary> CreateRevisionAsync(
        Guid previousSubmissionId,
        IntakeSubmission revisedSubmission,
        CancellationToken cancellationToken = default);

    Task<GuestSubmissionSummary> QueueAsync(Guid clientSubmissionId, CancellationToken cancellationToken = default);

    Task<GuestSubmissionSummary?> GetAsync(Guid clientSubmissionId, CancellationToken cancellationToken = default);

    Task<GuestDraftDetails?> GetDraftAsync(Guid clientSubmissionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GuestSubmissionSummary>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface IGuestIntakeTransport
{
    Task<IntakeSessionResponse> CreateSessionAsync(
        Guid sessionId,
        string receiptProof,
        CancellationToken cancellationToken);

    Task<IntakeReceipt> SubmitAsync(
        IntakeSubmission submission,
        Guid sessionId,
        string intakeCapability,
        string receiptProof,
        CancellationToken cancellationToken);

    Task<IntakeReceipt> GetReceiptAsync(
        Guid receiptId,
        string receiptProof,
        CancellationToken cancellationToken);

    Task<PublicIntakeMetadata> GetMetadataAsync(CancellationToken cancellationToken);

    Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken cancellationToken);
}

public interface IGuestSubmissionCoordinator
{
    Task<GuestSyncSummary> SubmitPendingAsync(CancellationToken cancellationToken = default);

    Task<GuestReceiptRefreshSummary> RefreshReceiptsAsync(CancellationToken cancellationToken = default);
}

public interface IGuestAudioStore
{
    Task<GuestAudioAsset> SaveOriginalAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        string mediaType,
        int durationMilliseconds,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadOriginalAsync(
        Guid clientSubmissionId,
        Guid attachmentId,
        CancellationToken cancellationToken = default);
}

public sealed class GuestSubmissionStoreUnavailableException : Exception
{
    public GuestSubmissionStoreUnavailableException() : base("A saved private report could not be accessed safely.") { }
}

