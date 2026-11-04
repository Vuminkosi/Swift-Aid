using SwiftAid.Contracts.Geography;
using System.Text.Json;

namespace SwiftAid.Contracts.Events;

/// <summary>Discriminated v2 report payload. Exactly one subject-specific section is populated.</summary>
public sealed record CaseReportV2Payload(
    string ReportKind,
    Guid ReportId,
    ReportContextV2Payload Context,
    ReportEvidenceV2Payload Evidence,
    ReportConfirmationsV2Payload Confirmations,
    HumanReportV2Payload? Human,
    AnimalReportV2Payload? Animal) : CaseEventPayload;

public sealed record CaseReportedV2Payload(string WorkspaceId, CaseReportV2Payload Report) : CaseEventPayload;

public sealed record CaseReportCorrectedV2Payload(
    CaseReportV2Payload Report,
    string Reason,
    long PreviousReportRevision) : CaseEventPayload;

public sealed record ReportContextV2Payload(
    ReportedFactV2Payload<ReportedTime> Occurrence,
    DateTimeOffset? CapturedAtUtc,
    string? CapturedOffset,
    string? CapturedZoneId,
    ReportedTimePrecision CapturePrecision,
    LocationContext Location,
    ReportedFactV2Payload<ReportedTime>? Onset = null);

public sealed record ReportedFactV2Payload<T>(
    ReportedValueV2State State,
    JsonElement? Value,
    ReportProvenanceV2Payload Provenance);

public enum ReportedValueV2State
{
    Unknown,
    NotRecorded,
    NotApplicable,
    Absent,
    Known
}

public enum ReportSourceV2Kind
{
    ReporterNarrative,
    ReporterObservation,
    OriginalAudio,
    WorkerConfirmed
}

public sealed record ReportProvenanceV2Payload(ReportSourceV2Kind Source, string StableReference);
public sealed record ReportedTextV2Payload(string Text, ReportProvenanceV2Payload Provenance);
public sealed record ReportEvidenceReferenceV2Payload(string Reference, ReportProvenanceV2Payload Provenance);

public sealed record ReportEvidenceV2Payload(
    ReportedTextV2Payload? Narrative,
    IReadOnlyList<ReportedTextV2Payload> Observations,
    ReportEvidenceReferenceV2Payload? OriginalAudio);

public sealed record ReportConfirmationsV2Payload(
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

public sealed record HumanReportV2Payload(
    ReportedFactV2Payload<string> PseudonymousSubjectReference,
    ReportedFactV2Payload<int> AgeYears,
    ReportedFactV2Payload<string> ReportedConcern);

public enum AnimalSubjectV2Scope
{
    Individual,
    Herd,
    Flock,
    OtherGroup
}

public sealed record AnimalCountV2Payload(
    ReportedValueV2State State,
    int? Value,
    ReportProvenanceV2Payload Provenance,
    string? PopulationReference,
    ReportedFactV2Payload<ReportedTime>? ObservationPeriod);

public sealed record AnimalReportV2Payload(
    AnimalSubjectV2Scope SubjectScope,
    ReportedFactV2Payload<string> Species,
    ReportedFactV2Payload<string> HoldingReference,
    AnimalCountV2Payload AnimalsAtRisk,
    AnimalCountV2Payload AnimalsAffected,
    AnimalCountV2Payload AnimalsDead);
