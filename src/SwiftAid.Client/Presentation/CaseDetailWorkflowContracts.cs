using SwiftAid.Contracts.Cases;
using System.Text;

namespace SwiftAid.Client.Presentation;

/// <summary>
/// Safe, case-scoped summary for the Case Details overview. Summary is a
/// bounded operational description and must never contain the original report.
/// </summary>
public sealed record CaseDetailSummary(
    Guid CaseId,
    string PublicReference,
    CaseWorkspaceSector SubjectType,
    string Summary,
    string ClassificationLabel,
    string Workflow,
    string PriorityLabel,
    string CountryLabel,
    string LocationLabel,
    string? OwnerDisplayLabel,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsValid =>
        CaseId != Guid.Empty &&
        IsBounded(PublicReference, 1, 80) &&
        SubjectType is CaseWorkspaceSector.Human or CaseWorkspaceSector.Animal &&
        IsBounded(Summary, 1, 300) &&
        IsBounded(ClassificationLabel, 1, 120) &&
        IsBounded(Workflow, 1, 80) &&
        IsBounded(PriorityLabel, 1, 40) &&
        IsBounded(CountryLabel, 1, 120) &&
        IsBounded(LocationLabel, 1, 160) &&
        (OwnerDisplayLabel is null || IsBounded(OwnerDisplayLabel, 1, 120)) &&
        OpenedAtUtc.Offset == TimeSpan.Zero &&
        UpdatedAtUtc.Offset == TimeSpan.Zero &&
        OpenedAtUtc <= UpdatedAtUtc;

    private static bool IsBounded(string value, int minimumLength, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length >= minimumLength && value.Length <= maximumLength &&
        !value.Any(char.IsControl);
}

/// <summary>Authorized per-case overview projection. Implementations enforce case access.</summary>
public interface ICaseDetailSummaryClient
{
    Task<UiCapabilityResult<CaseDetailSummary>> GetAsync(
        Guid caseId,
        CancellationToken cancellationToken = default);
}

/// <summary>A safe, case-authorized mention target. Reference is opaque and server-resolved.</summary>
public sealed record CaseDiscussionMentionTarget(string Reference, string DisplayLabel)
{
    public bool IsValid =>
        IsBounded(Reference, 1, 160) &&
        IsBounded(DisplayLabel, 1, 120);

    private static bool IsBounded(string value, int minimumLength, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length >= minimumLength && value.Length <= maximumLength &&
        !value.Any(char.IsControl);
}

/// <summary>
/// Append-only internal discussion command. Mention references and evidence keys
/// must be re-resolved against the current authorized case scope by the service.
/// SubmissionId provides a stable idempotency identity scoped by case and the
/// authenticated actor. Before checking ExpectedVersion, services must compare a
/// canonical payload fingerprint: exact replay returns the original result even
/// when the version is now stale; reuse with a different payload returns Conflict.
/// The fingerprint includes ExpectedVersion, normalized message text, and sorted
/// mention/evidence references. Actor identity comes from the authenticated context.
/// </summary>
public sealed record CaseDiscussionPostCommand(
    Guid CaseId,
    Guid SubmissionId,
    long ExpectedVersion,
    string Message,
    IReadOnlyList<string> MentionReferences,
    IReadOnlyList<string> EvidenceKeys)
{
    public const int MaximumMessageLength = 4_000;
    public const int MaximumMentions = 20;
    public const int MaximumEvidenceReferences = 10;

    public bool IsValid =>
        CaseId != Guid.Empty && SubmissionId != Guid.Empty && ExpectedVersion >= 0 &&
        !string.IsNullOrWhiteSpace(Message) && Message.Length <= MaximumMessageLength &&
        !Message.Any(IsDisallowedControl) &&
        HasWellFormedUnicode(Message) &&
        AreUniqueBounded(MentionReferences, MaximumMentions, 160) &&
        AreUniqueBounded(EvidenceKeys, MaximumEvidenceReferences, 160);

    /// <summary>Canonical message form used for idempotency fingerprints.</summary>
    public string GetCanonicalMessage() =>
        Message.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC)
            .Trim();

    /// <summary>Returns reference sets in stable order for idempotency fingerprints.</summary>
    public IReadOnlyList<string> GetCanonicalMentionReferences() =>
        MentionReferences.OrderBy(reference => reference, StringComparer.Ordinal).ToArray();

    /// <summary>Returns evidence references in stable order for idempotency fingerprints.</summary>
    public IReadOnlyList<string> GetCanonicalEvidenceKeys() =>
        EvidenceKeys.OrderBy(reference => reference, StringComparer.Ordinal).ToArray();

    private static bool IsDisallowedControl(char value) =>
        char.IsControl(value) && value is not '\r' and not '\n';

    private static bool HasWellFormedUnicode(string value)
    {
        try
        {
            _ = value.Normalize(NormalizationForm.FormC);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool AreUniqueBounded(IReadOnlyList<string>? values, int maximumCount, int maximumLength) =>
        values is not null && values.Count <= maximumCount &&
        values.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl)) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count;
}

/// <summary>Accepted internal discussion append result; contains no message body.</summary>
public sealed record CaseDiscussionPostResult(
    Guid CaseId,
    Guid SubmissionId,
    string MessageKey,
    long AggregateVersion,
    DateTimeOffset RecordedAtUtc)
{
    public bool IsValid =>
        CaseId != Guid.Empty && SubmissionId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(MessageKey) && MessageKey.Length <= 160 &&
        AggregateVersion >= 0 && RecordedAtUtc.Offset == TimeSpan.Zero;
}

/// <summary>
/// Write capability for case-internal discussion. Public channels are never a
/// destination for this operation. Implementations enforce case access and resolve
/// every mention/evidence reference again before append.
/// </summary>
public interface ICaseDiscussionCommandClient
{
    Task<UiCapabilityResult<IReadOnlyList<CaseDiscussionMentionTarget>>> GetMentionTargetsAsync(
        Guid caseId,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<CaseDiscussionPostResult>> AppendAsync(
        CaseDiscussionPostCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Metadata for a bounded file upload. Declared MIME type and length are untrusted
/// hints; implementations must bound actual stream reads, inspect file signatures,
/// derive the stored MIME type, and enforce case authorization.
/// </summary>
public sealed record CaseEvidenceUploadRequest(
    Guid CaseId,
    Guid SubmissionId,
    long ExpectedVersion,
    string FileName,
    string DeclaredContentType,
    long DeclaredContentLength)
{
    public const int MaximumContentBytes = 10 * 1024 * 1024;

    public bool IsValid =>
        CaseId != Guid.Empty && SubmissionId != Guid.Empty && ExpectedVersion >= 0 &&
        CaseEvidenceFileRules.IsSafeFileName(FileName) &&
        CaseEvidenceFileRules.HasSupportedFileType(FileName, DeclaredContentType) &&
        DeclaredContentLength is > 0 and <= MaximumContentBytes;

    /// <summary>
    /// Upload idempotency is scoped by case and authenticated actor. The service
    /// reads no more than MaximumContentBytes, computes the actual SHA-256 and
    /// detected MIME type, then fingerprints case, actor scope, SubmissionId,
    /// ExpectedVersion, normalized filename, detected type, byte length and hash.
    /// Exact replay returns the stored result before checking the now-stale version;
    /// same key with another fingerprint returns Conflict. Declared MIME is not trusted.
    /// </summary>
    public string GetCanonicalFileName() => FileName.Trim().Normalize(NormalizationForm.FormC);
}

/// <summary>Validated, bounded metadata for an authorized evidence file.</summary>
public sealed record CaseEvidenceContentMetadata(
    Guid CaseId,
    string EvidenceKey,
    string FileName,
    string ContentType,
    long ByteLength,
    string Sha256,
    long AggregateVersion)
{
    public bool IsValid =>
        CaseId != Guid.Empty && !string.IsNullOrWhiteSpace(EvidenceKey) && EvidenceKey.Length <= 160 &&
        CaseEvidenceFileRules.IsSafeFileName(FileName) &&
        CaseEvidenceFileRules.HasSupportedFileType(FileName, ContentType) &&
        ByteLength is > 0 and <= CaseEvidenceUploadRequest.MaximumContentBytes &&
        Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit) &&
        AggregateVersion >= 0;
}

/// <summary>Accepted attachment metadata; file content is never returned.</summary>
public sealed record CaseEvidenceUploadResult(
    Guid CaseId,
    Guid SubmissionId,
    long AggregateVersion,
    CaseEvidenceContentMetadata Evidence)
{
    public bool IsValid =>
        CaseId != Guid.Empty && SubmissionId != Guid.Empty && AggregateVersion >= 0 &&
        Evidence is { IsValid: true } && Evidence.CaseId == CaseId && Evidence.AggregateVersion == AggregateVersion;
}

/// <summary>
/// Case-authorized attachment upload capability. Implementations must bound reads
/// from the supplied stream even if declared length is inaccurate; inspect signatures,
/// derive the stored MIME type, and hash bytes before committing the metadata/result.
/// </summary>
public interface ICaseEvidenceUploadClient
{
    Task<UiCapabilityResult<CaseEvidenceUploadResult>> UploadAsync(
        CaseEvidenceUploadRequest request,
        Stream content,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authorized, bounded streaming access to evidence bytes. Successful completion
/// means exactly ByteLength bytes were written and match the returned SHA-256. A
/// failure can leave partial bytes in destination; callers must discard them.
/// Implementations enforce current case access and must cap the transfer at 10 MiB.
/// </summary>
public interface ICaseEvidenceContentClient
{
    Task<UiCapabilityResult<CaseEvidenceContentMetadata>> DownloadToAsync(
        Guid caseId,
        string evidenceKey,
        Stream destination,
        CancellationToken cancellationToken = default);
}

internal static class CaseEvidenceFileRules
{
    public static bool IsSafeFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName.Length <= 100 &&
        HasWellFormedUnicode(fileName) &&
        !fileName.Any(char.IsControl) &&
        !fileName.Any(character => character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') &&
        !fileName.EndsWith('.') && !fileName.EndsWith(' ') &&
        fileName is not "." and not "..";

    private static bool HasWellFormedUnicode(string value)
    {
        try
        {
            _ = value.Normalize(NormalizationForm.FormC);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool HasSupportedFileType(string? fileName, string? contentType)
    {
        if (!IsSafeFileName(fileName) || string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName!).ToLowerInvariant();
        var mediaType = contentType.Trim().ToLowerInvariant();
        return (extension, mediaType) switch
        {
            (".jpg" or ".jpeg", "image/jpeg") => true,
            (".png", "image/png") => true,
            (".pdf", "application/pdf") => true,
            (".txt", "text/plain" or "text/plain; charset=utf-8") => true,
            _ => false
        };
    }
}

public enum CaseObservationCategory
{
    Clinical,
    Environmental,
    Community
}

/// <summary>One attributed, descriptive observation; it does not state a diagnosis or urgency.</summary>
public sealed record CaseObservationRecord(
    string ObservationKey,
    CaseObservationCategory Category,
    string Observation,
    string SourceLabel,
    IReadOnlyList<string> EvidenceKeys,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string ActorDisplayLabel,
    long AggregateVersion)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ObservationKey) && ObservationKey.Length <= 160 &&
        Enum.IsDefined(Category) && IsBounded(Observation, 1, 4_000) &&
        IsBounded(SourceLabel, 1, 160) &&
        AreUniqueBounded(EvidenceKeys, CaseObservationCommand.MaximumEvidenceReferences, 160) &&
        (OccurredAtUtc is null || OccurredAtUtc.Value.Offset == TimeSpan.Zero) &&
        (OccurredAtUtc is null || OccurredAtUtc.Value <= RecordedAtUtc) &&
        RecordedAtUtc.Offset == TimeSpan.Zero &&
        IsBounded(ActorDisplayLabel, 1, 120) && AggregateVersion >= 0;

    private static bool IsBounded(string value, int minimumLength, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length >= minimumLength && value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool AreUniqueBounded(IReadOnlyList<string>? values, int maximumCount, int maximumLength) =>
        values is not null && values.Count <= maximumCount &&
        values.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl)) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count;
}

public sealed record CaseObservationSnapshot(
    Guid CaseId,
    long AggregateVersion,
    Page<CaseObservationRecord> Items)
{
    public bool IsValid =>
        CaseId != Guid.Empty && AggregateVersion >= 0 &&
        Items is { Items: not null, PageNumber: > 0 and <= 100_000, PageSize: > 0 and <= 100, TotalCount: >= 0 } &&
        Items.Items.Count <= Items.PageSize && Items.TotalCount >= Items.Items.Count &&
        Items.Items.All(item => item is { IsValid: true });
}

/// <summary>
/// Descriptive observation command preserving source and evidence provenance.
/// Idempotency is scoped by case and authenticated actor plus SubmissionId. The
/// canonical fingerprint includes ExpectedVersion, category, normalized narrative
/// and source, occurred time, and sorted evidence keys. Exact replay returns the
/// original result before stale-version validation; a changed payload returns Conflict.
/// </summary>
public sealed record CaseObservationCommand(
    Guid CaseId,
    Guid SubmissionId,
    long ExpectedVersion,
    CaseObservationCategory Category,
    string Observation,
    string SourceLabel,
    IReadOnlyList<string> EvidenceKeys,
    DateTimeOffset? OccurredAtUtc)
{
    public const int MaximumEvidenceReferences = 10;

    public bool IsValid =>
        CaseId != Guid.Empty && SubmissionId != Guid.Empty && ExpectedVersion >= 0 &&
        Enum.IsDefined(Category) && IsBounded(Observation, 1, 4_000) &&
        IsBounded(SourceLabel, 1, 160) &&
        AreUniqueBounded(EvidenceKeys, MaximumEvidenceReferences, 160) &&
        (OccurredAtUtc is null || OccurredAtUtc.Value.Offset == TimeSpan.Zero);

    private static bool IsBounded(string value, int minimumLength, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length >= minimumLength && value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static bool AreUniqueBounded(IReadOnlyList<string>? values, int maximumCount, int maximumLength) =>
        values is not null && values.Count <= maximumCount &&
        values.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl)) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count;
}

public sealed record CaseObservationWriteResult(
    Guid CaseId,
    Guid SubmissionId,
    string ObservationKey,
    long AggregateVersion,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? OccurredAtUtc = null)
{
    public bool IsValid =>
        CaseId != Guid.Empty && SubmissionId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(ObservationKey) && ObservationKey.Length <= 160 &&
        AggregateVersion >= 0 && RecordedAtUtc.Offset == TimeSpan.Zero &&
        (OccurredAtUtc is null || OccurredAtUtc.Value.Offset == TimeSpan.Zero) &&
        (OccurredAtUtc is null || OccurredAtUtc.Value <= RecordedAtUtc);
}

/// <summary>Case-authorized descriptive observation read/write capability.</summary>
public interface ICaseObservationClient
{
    Task<UiCapabilityResult<CaseObservationSnapshot>> GetAsync(
        Guid caseId,
        PageRequest page,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<CaseObservationWriteResult>> RecordAsync(
        CaseObservationCommand command,
        CancellationToken cancellationToken = default);
}
