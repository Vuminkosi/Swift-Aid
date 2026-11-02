namespace SwiftAid.UI.Components.Surfaces.Operations.CaseDetail;

public enum CaseDetailSubjectType { Human, Animal, Unknown }
public enum CaseDetailObservationCategory { Clinical, Environmental, Community }
public enum CaseDetailOperationStatus { Accepted, Replayed, Conflict, Denied, Offline, Unavailable, Failed }

public sealed record CaseDetailSummary(
    Guid CaseId, string PublicReference, CaseDetailSubjectType SubjectType, string Summary,
    string ClassificationLabel, string Workflow, string PriorityLabel, string CountryLabel,
    string LocationLabel, string? OwnerDisplayLabel, DateTimeOffset OpenedAtUtc, DateTimeOffset UpdatedAtUtc,
    string LaboratoryLabel = "Unavailable", string OutcomeStateLabel = "Unavailable");

public sealed record CaseDetailMentionTarget(string Reference, string DisplayLabel);
public sealed record CaseDetailCommandOutcome(CaseDetailOperationStatus Status, long AggregateVersion = 0);
public enum CaseDetailCorrectionKind { LegacyV1 = 0, HumanV2 = 1, AnimalV2 = 2 }
public sealed record CaseDetailCorrectionView(CaseDetailPanelState State, long AggregateVersion = 0,
    long ReportRevision = 0, CaseDetailCorrectionKind ReportKind = CaseDetailCorrectionKind.LegacyV1,
    bool CanCorrectReport = false, string? Narrative = null);
public sealed record CaseDetailCorrectionRequest(Guid CaseId, Guid CommandId, long ExpectedVersion,
    string Narrative, string Reason);
public sealed record CaseDetailCorrectionOutcome(CaseDetailOperationStatus Status,
    long AggregateVersion = 0, long ReportRevision = 0);
public sealed record CaseDetailDiscussionPostRequest(Guid CaseId, Guid SubmissionId, long ExpectedVersion,
    string Message, IReadOnlyList<string> MentionReferences, IReadOnlyList<string> EvidenceKeys);
public sealed record CaseDetailEvidenceUploadRequest(Guid CaseId, Guid SubmissionId, long ExpectedVersion,
    string FileName, string ContentType, long ContentLength)
{
    public bool IsValid =>
        CaseId != Guid.Empty && SubmissionId != Guid.Empty && ExpectedVersion >= 0 &&
        !string.IsNullOrWhiteSpace(FileName) && FileName.Length <= 100 &&
        !FileName.Any(char.IsControl) &&
        !FileName.Any(character => character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') &&
        ContentLength is > 0 and <= 10 * 1024 * 1024 && IsSupportedType(FileName, ContentType);

    private static bool IsSupportedType(string fileName, string contentType) =>
        (Path.GetExtension(fileName).ToLowerInvariant(), contentType.Trim().ToLowerInvariant()) switch
        {
            (".jpg" or ".jpeg", "image/jpeg") => true,
            (".png", "image/png") => true,
            (".pdf", "application/pdf") => true,
            (".txt", "text/plain" or "text/plain; charset=utf-8") => true,
            _ => false
        };
}
public sealed record CaseDetailObservationRecord(string ObservationKey, CaseDetailObservationCategory Category,
    string Observation, string SourceLabel, IReadOnlyList<string> EvidenceKeys, DateTimeOffset? OccurredAtUtc,
    DateTimeOffset RecordedAtUtc, string ActorDisplayLabel, long AggregateVersion);
public sealed record CaseDetailObservationRequest(Guid CaseId, Guid SubmissionId, long ExpectedVersion,
    CaseDetailObservationCategory Category, string Observation, string SourceLabel,
    IReadOnlyList<string> EvidenceKeys, DateTimeOffset? OccurredAtUtc);

/// <summary>Human-readable case overview fields safe for the shared interface.</summary>
public sealed record CaseDetailOverviewView(
    CaseDetailPanelState State,
    CaseDetailSummary? Summary);

/// <summary>Discussion content supplied by the host's selected case-data adapter.</summary>
public sealed record CaseDetailDiscussionView(
    CaseDetailPanelState State,
    IReadOnlyList<CaseDetailDiscussionMessage> Messages,
    long AggregateVersion = 0);

/// <summary>Case-scoped eligible mention targets loaded from the authorized service.</summary>
public sealed record CaseDetailMentionTargetsView(
    CaseDetailPanelState State,
    IReadOnlyList<CaseDetailMentionTarget> Targets);

/// <summary>Attributed observations supplied by the selected case-data adapter.</summary>
public sealed record CaseDetailObservationsView(
    CaseDetailPanelState State,
    IReadOnlyList<CaseDetailObservationRecord> Records,
    long AggregateVersion = 0);

/// <summary>Audio and file content supplied by the host's selected case-data adapter.</summary>
public sealed record CaseDetailEvidenceView(
    CaseDetailPanelState State,
    IReadOnlyList<CaseDetailEvidenceItem> Items,
    string? DownloadingEvidenceKey = null,
    string? DownloadMessage = null,
    bool DownloadFailed = false,
    long AggregateVersion = 0);
