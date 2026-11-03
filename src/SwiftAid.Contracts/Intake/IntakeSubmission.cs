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
    IntakeConfirmations Confirmations);

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
    string PrivacyNoticeReference)
{
    /// <summary>Current server-side text limits; null when served by an older Gateway.</summary>
    public PublicIntakeTextLimits? TextLimits { get; init; }
}

/// <summary>Public, non-sensitive validation bounds for report text.</summary>
public sealed record PublicIntakeTextLimits(int MaximumNarrativeCharacters, int MaximumObservations, int MaximumObservationCharacters)
{
    public bool IsValid() => MaximumNarrativeCharacters is >= 1 and <= 20000 &&
        MaximumObservations is >= 1 and <= 100 && MaximumObservationCharacters is >= 1 and <= 2000;
}

public sealed record PublicIntakeCountry(string Code, string Label);

public sealed record PublicIntakeCountryCatalogue(
    string Version,
    string Reference,
    IReadOnlyList<PublicIntakeCountry> Countries);
