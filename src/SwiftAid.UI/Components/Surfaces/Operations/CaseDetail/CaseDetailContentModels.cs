namespace SwiftAid.UI.Components.Surfaces.Operations.CaseDetail;

public enum CaseDetailPanelState
{
    Loading,
    Empty,
    Populated,
    Unavailable,
    Denied,
    Offline,
    Error
}

public sealed record CaseDetailDiscussionMessage(
    string AuthorLabel,
    string Message,
    DateTimeOffset RecordedAtUtc,
    IReadOnlyList<string>? MentionReferences = null,
    IReadOnlyList<string>? EvidenceKeys = null);

public enum CaseDetailEvidenceKind
{
    Audio,
    File
}

public enum CaseDetailLocalMediaState
{
    AvailableOnDevice,
    StoredOnDevice,
    NotAvailableOnDevice,
    Unknown
}

public enum CaseDetailUploadState
{
    NotStarted,
    Uploading,
    Uploaded,
    Failed,
    Unavailable
}

public sealed record CaseDetailEvidenceItem(
    string EvidenceKey,
    string DisplayName,
    CaseDetailEvidenceKind Kind,
    DateTimeOffset? RecordedAtUtc,
    CaseDetailLocalMediaState LocalMediaState,
    CaseDetailUploadState UploadState);
