using SwiftAid.Contracts.Geography;

namespace SwiftAid.Contracts.Intake;

/// <summary>Unverified public report input; carries no organization actor or staff authority.</summary>
public sealed record IntakeSubmission(
    Guid ClientSubmissionId,
    IntakeSubjectScope SubjectScope,
    string? Narrative,
    IReadOnlyList<string> Observations,
    string? OriginalAudioReference,
    DateTimeOffset? CapturedAtUtc,
    string? CapturedOffset,
    string? CapturedZoneId,
    ReportedTimePrecision CapturePrecision,
    ReportedTime Occurrence,
    LocationContext Location,
    bool ProcessingConfirmed,
    bool ReporterConfirmedOccurrenceCountry);

public enum IntakeSubjectScope
{
    Human,
    Individual,
    Herd,
    Flock,
    OtherGroup
}
