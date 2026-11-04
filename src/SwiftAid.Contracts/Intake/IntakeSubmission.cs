using SwiftAid.Contracts.Geography;

namespace SwiftAid.Contracts.Intake;

/// <summary>Unverified public report input; carries no organization actor or staff authority.</summary>
public sealed record IntakeSubmission(
    int SchemaVersion,
    Guid ClientSubmissionId,
    IntakeSubjectScope SubjectScope,
    string? Narrative,
    IReadOnlyList<string> Observations,
    IReadOnlyList<Guid> OriginalAudioAttachmentIds,
    ReportedTime CapturedAt,
    ReportedTime Occurrence,
    LocationContext Location,
    IntakeConfirmations Confirmations)
{
    /// <summary>Age in completed years as reported for a human subject; null means unknown.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? AgeYears { get; init; }
}

public enum IntakeSubjectScope
{
    Human,
    Individual,
    Herd,
    Flock,
    OtherGroup
}

/// <summary>Explicit acknowledgements attached to a guest intake, mirroring managed report confirmations.</summary>
public sealed record IntakeConfirmations(
    bool ReportReviewConfirmed,
    string OccurrenceCountryCode,
    string OccurrenceCountryCatalogueVersion,
    bool OccurrenceCountryConfirmed,
    bool ProcessingConfirmed,
    string ProcessingPolicyReference,
    string ProcessingPolicyVersion,
    bool? RecordingConfirmed,
    string? RecordingPolicyReference,
    string? RecordingPolicyVersion);

public enum IntakeStatus
{
    ReceivedUnverified,
    AwaitingReview,
    AcceptedIntoCase,
    LinkedAsEvidence,
    Declined
}

public enum IntakeRoutingStatus
{
    NeedsRouting,
    RoutedForReview,
    NoServiceCoverage
}

/// <summary>Minimal reporter-owned receipt. Contains no case, staff, diagnosis or assignment data.</summary>
public sealed record IntakeReceipt(
    Guid ReceiptId,
    Guid ClientSubmissionId,
    IntakeStatus Status,
    IntakeRoutingStatus RoutingStatus,
    DateTimeOffset ReceivedAtUtc);

/// <summary>Opaque short-lived credential returned by the intake session endpoint.</summary>
public sealed record IntakeSessionResponse(string Capability, DateTimeOffset ExpiresAtUtc);

/// <summary>Reusable guest session request; its proof is hashed before crossing the application storage port.</summary>
public sealed record CreateIntakeSessionRequest(Guid SessionId, string ReceiptProof);

/// <summary>Receipt lookup payload; proof is carried separately in the protected request header.</summary>
public sealed record IntakeReceiptQuery(Guid ReceiptId);

/// <summary>Public metadata contains only versioned catalogue/privacy references and country labels.</summary>
public sealed record PublicIntakeMetadata(
    string Version,
    string CountryCatalogueVersion,
    string CountryCatalogueReference,
    string PrivacyNoticeVersion,
    string PrivacyNoticeReference);

public sealed record PublicIntakeCountry(string Code, string Label);

public sealed record PublicIntakeCountryCatalogue(
    string Version,
    string Reference,
    IReadOnlyList<PublicIntakeCountry> Countries);
