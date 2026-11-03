using SwiftAid.Contracts.Intake;
using System.Text.Json;

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

/// <summary>Private editable form state inside the protected local envelope. It is never sent to intake.</summary>
public sealed record GuestDraftFormSnapshot(int SchemaVersion, string Json)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumJsonCharacters = 131072;

    public bool IsValid()
    {
        if (SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(Json) || Json.Length > MaximumJsonCharacters)
            return false;
        try
        {
            using var document = JsonDocument.Parse(Json, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    public override string ToString() => $"GuestDraftFormSnapshot {{ SchemaVersion = {SchemaVersion}, Json = [redacted] }}";
}

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
    IReadOnlyList<GuestAudioAsset>? AudioAssets = null,
    long Version = 1,
    GuestDraftFormSnapshot? FormSnapshot = null);

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
    DateTimeOffset UpdatedAtUtc,
    long Version = 1);

/// <summary>Editable local draft including private raw form data; never a server submission.</summary>
public sealed record GuestEditableDraftDetails(Guid ClientSubmissionId, IntakeSubmission Submission,
    GuestDraftFormSnapshot FormSnapshot, GuestSubmissionLocalState State, long Version);

/// <summary>A guest submission write guarded by the version the caller previously read.</summary>
/// <param name="Record">The complete protected record to persist.</param>
/// <param name="ExpectedVersion">Null requires a new record; a positive value must match the current version.</param>
public sealed record GuestSubmissionWrite(GuestSubmissionRecord Record, long? ExpectedVersion);

/// <summary>Raised when a versioned guest write no longer matches the stored record.</summary>
public sealed class GuestSubmissionPersistenceConflictException(Guid clientSubmissionId)
    : Exception("The saved report changed. Refresh it before trying again.")
{
    public Guid ClientSubmissionId { get; } = clientSubmissionId;
}

public sealed record GuestSyncSummary(int Queued, int Received, int RetryPending, int NeedsAttention);

public sealed record GuestReceiptRefreshSummary(int Updated, int RetryPending);

public interface IGuestSubmissionPersistence
{
    Task<GuestSubmissionRecord?> GetAsync(Guid clientSubmissionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GuestSubmissionRecord>> GetAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(GuestSubmissionRecord submission, CancellationToken cancellationToken);

    Task SaveBatchAsync(IReadOnlyList<GuestSubmissionRecord> submissions, CancellationToken cancellationToken);

    async Task SaveVersionedAsync(
        GuestSubmissionRecord submission,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        await SaveAsync(submission, cancellationToken).ConfigureAwait(false);
    }

    async Task SaveBatchVersionedAsync(
        IReadOnlyList<GuestSubmissionWrite> writes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        await SaveBatchAsync(writes.Select(write => write.Record).ToArray(), cancellationToken).ConfigureAwait(false);
    }
}

public interface IGuestSubmissionStore
{
    Task<GuestSubmissionSummary> CreateDraftAsync(IntakeSubmission submission, CancellationToken cancellationToken = default);

    Task<GuestSubmissionSummary> CreateEditableDraftAsync(IntakeSubmission carrier, GuestDraftFormSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<GuestSubmissionSummary> CreateEditableRevisionAsync(Guid previousSubmissionId, IntakeSubmission carrier,
        GuestDraftFormSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<GuestSubmissionSummary> CompleteEditableDraftAsync(Guid previousSubmissionId, IntakeSubmission submission,
        CancellationToken cancellationToken = default);

    Task<GuestEditableDraftDetails?> GetEditableDraftAsync(Guid clientSubmissionId, CancellationToken cancellationToken = default);

    Task<GuestSubmissionSummary> CreateRevisionAsync(
        Guid previousSubmissionId,
        IntakeSubmission revisedSubmission,
        CancellationToken cancellationToken = default);

    Task<GuestAudioRetakeResult> RetakeAudioAsync(
        Guid submissionId,
        Guid attachmentId,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    Task<GuestAudioCleanupRetrySummary> RetryPendingAudioCleanupAsync(
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

    Task<GuestAudioCleanupResult> CleanupSupersededOriginalAsync(
        GuestAudioCleanupIntent intent,
        CancellationToken cancellationToken = default);
}

public sealed class GuestSubmissionStoreUnavailableException : Exception
{
    public GuestSubmissionStoreUnavailableException() : base("A saved private report could not be accessed safely.") { }
}

