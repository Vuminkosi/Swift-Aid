namespace SwiftAid.Client.Presentation;

/// <summary>Bounded page request for a case discussion read.</summary>
public sealed record CaseDiscussionQuery(PageRequest Page)
{
    public bool IsValid => Page is { IsValid: true, PageNumber: <= 100_000 };
}

/// <summary>Bounded page request for a case evidence metadata read.</summary>
public sealed record CaseEvidenceQuery(PageRequest Page)
{
    public bool IsValid => Page is { IsValid: true, PageNumber: <= 100_000 };
}

public sealed record CaseDiscussionMessage(
    string MessageKey,
    string AuthorLabel,
    string Message,
    DateTimeOffset RecordedAtUtc,
    IReadOnlyList<string>? MentionReferences = null,
    IReadOnlyList<string>? EvidenceKeys = null)
{
    public const int MaximumMentionReferences = 20;
    public const int MaximumEvidenceKeys = 10;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(MessageKey) && MessageKey.Length <= 160 &&
        !string.IsNullOrWhiteSpace(AuthorLabel) && AuthorLabel.Length <= 120 &&
        !string.IsNullOrWhiteSpace(Message) && Message.Length <= 4_000 &&
        RecordedAtUtc.Offset == TimeSpan.Zero &&
        AreValidReferences(MentionReferences, MaximumMentionReferences) &&
        AreValidReferences(EvidenceKeys, MaximumEvidenceKeys);

    private static bool AreValidReferences(IReadOnlyList<string>? references, int maximumCount) =>
        references is null ||
        (references.Count <= maximumCount &&
         references.All(reference => !string.IsNullOrWhiteSpace(reference) &&
             reference.Length <= 160 && !reference.Any(char.IsControl)) &&
         references.Distinct(StringComparer.Ordinal).Count() == references.Count);
}

public sealed record CaseDiscussionSnapshot(
    Guid CaseId,
    long AggregateVersion,
    Page<CaseDiscussionMessage> Messages)
{
    public bool IsValid =>
        CaseId != Guid.Empty && AggregateVersion >= 0 &&
        Messages is { Items: not null, PageNumber: > 0 and <= 100_000, PageSize: > 0 and <= 100, TotalCount: >= 0 } &&
        Messages.Items.Count <= Messages.PageSize && Messages.TotalCount >= Messages.Items.Count &&
        Messages.Items.All(message => message is { IsValid: true });
}

public enum CaseEvidenceKind
{
    Audio,
    File
}

public enum CaseEvidenceLocalMediaState
{
    AvailableOnDevice,
    StoredOnDevice,
    NotAvailableOnDevice,
    Unknown
}

public enum CaseEvidenceUploadState
{
    NotStarted,
    Uploading,
    Uploaded,
    Failed,
    Unavailable
}

/// <summary>Safe evidence metadata; the DTO never contains media bytes or a playback URL.</summary>
public sealed record CaseEvidenceReference(
    string EvidenceKey,
    string DisplayName,
    CaseEvidenceKind Kind,
    DateTimeOffset? RecordedAtUtc,
    CaseEvidenceLocalMediaState LocalMediaState,
    CaseEvidenceUploadState UploadState)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(EvidenceKey) && EvidenceKey.Length <= 160 &&
        IsSafeFileName(DisplayName) && Enum.IsDefined(Kind) &&
        (RecordedAtUtc is null || RecordedAtUtc.Value.Offset == TimeSpan.Zero) &&
        Enum.IsDefined(LocalMediaState) && Enum.IsDefined(UploadState);

    internal static bool IsSafeFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && fileName.Length <= 100 &&
        !fileName.Any(char.IsControl) && !fileName.Contains('/') && !fileName.Contains('\\');
}

public sealed record CaseEvidenceSnapshot(
    Guid CaseId,
    long AggregateVersion,
    Page<CaseEvidenceReference> Items)
{
    public bool IsValid =>
        CaseId != Guid.Empty && AggregateVersion >= 0 &&
        Items is { Items: not null, PageNumber: > 0 and <= 100_000, PageSize: > 0 and <= 100, TotalCount: >= 0 } &&
        Items.Items.Count <= Items.PageSize && Items.TotalCount >= Items.Items.Count &&
        Items.Items.All(item => item is { IsValid: true });
}

/// <summary>Metadata-only evidence detail; media bytes are retrieved through a separate authorized service.</summary>
public sealed record CaseEvidenceDetail(
    Guid CaseId,
    long AggregateVersion,
    CaseEvidenceReference Evidence,
    string ContentType,
    long ByteLength,
    string Sha256)
{
    public bool IsValid =>
        CaseId != Guid.Empty && AggregateVersion >= 0 && Evidence is { IsValid: true } &&
        !string.IsNullOrWhiteSpace(ContentType) && ContentType.Length <= 128 &&
        ByteLength is > 0 and <= CaseEvidenceUploadRequest.MaximumContentBytes &&
        Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit);
}

public sealed record CaseEvidenceDownload(
    Guid CaseId,
    long AggregateVersion,
    string EvidenceKey,
    string FileName,
    string ContentType,
    long ByteLength,
    string Sha256,
    byte[] Content)
{
    public const int MaximumContentBytes = 16 * 1024;

    public bool IsValid =>
        CaseId != Guid.Empty && AggregateVersion >= 0 &&
        !string.IsNullOrWhiteSpace(EvidenceKey) && EvidenceKey.Length <= 160 &&
        CaseEvidenceReference.IsSafeFileName(FileName) &&
        string.Equals(ContentType, "text/plain; charset=utf-8", StringComparison.Ordinal) &&
        ByteLength is > 0 and <= MaximumContentBytes &&
        Content is { Length: > 0 and <= MaximumContentBytes } &&
        Content.LongLength == ByteLength &&
        Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit) &&
        string.Equals(Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Content)), StringComparison.Ordinal);
}

/// <summary>Read-only discussion capability for an authorized case.</summary>
public interface ICaseDiscussionClient
{
    Task<UiCapabilityResult<CaseDiscussionSnapshot>> GetAsync(
        Guid caseId,
        CaseDiscussionQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>Read-only evidence metadata capability for an authorized case.</summary>
public interface ICaseEvidenceClient
{
    Task<UiCapabilityResult<CaseEvidenceSnapshot>> GetAsync(
        Guid caseId,
        CaseEvidenceQuery query,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<CaseEvidenceDetail>> GetDetailAsync(
        Guid caseId,
        string evidenceKey,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<CaseEvidenceDownload>> DownloadAsync(
        Guid caseId,
        string evidenceKey,
        CancellationToken cancellationToken = default);
}
