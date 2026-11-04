using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Domain.Intake;

/// <summary>Validates untrusted public report facts without classifying or interpreting them.</summary>
public static class IntakeSubmissionRules
{
    public static void Validate(IntakeSubmission submission, IntakeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(submission);
        limits ??= IntakeLimits.Default;
        limits.Validate();
        if (submission.SchemaVersion != 1 || submission.ClientSubmissionId == Guid.Empty ||
            !Enum.IsDefined(submission.SubjectScope) || submission.AgeYears < 0 ||
            submission.AgeYears.HasValue && submission.SubjectScope != IntakeSubjectScope.Human)
        {
            throw new ArgumentException("The intake report is invalid.", nameof(submission));
        }

        var narrative = submission.Narrative;
        if (narrative is { } text && text.Length > limits.MaximumNarrativeCharacters ||
            narrative is not null && !string.Equals(narrative, narrative.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The intake report is invalid.", nameof(submission));
        }

        if (submission.Observations is null || submission.Observations.Count > limits.MaximumObservations ||
            submission.Observations.Any(value => string.IsNullOrWhiteSpace(value) ||
                value.Length > limits.MaximumObservationCharacters ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal)))
        {
            throw new ArgumentException("The intake report is invalid.", nameof(submission));
        }

        if (submission.OriginalAudioAttachmentIds is null ||
            submission.OriginalAudioAttachmentIds.Count > limits.MaximumAudioAttachments ||
            submission.OriginalAudioAttachmentIds.Any(id => id == Guid.Empty) ||
            submission.OriginalAudioAttachmentIds.Distinct().Count() != submission.OriginalAudioAttachmentIds.Count ||
            (string.IsNullOrWhiteSpace(narrative) && submission.Observations.Count == 0 &&
             submission.OriginalAudioAttachmentIds.Count == 0))
        {
            throw new ArgumentException("The intake report is invalid.", nameof(submission));
        }

        ValidateTime(submission.CapturedAt);
        ValidateTime(submission.Occurrence);
        ValidateLocation(submission.Location);
        ValidateConfirmations(submission.Confirmations, submission.Location.CountryCode, submission.Location.CatalogueVersion!,
            submission.OriginalAudioAttachmentIds.Count > 0);
    }

    private static void ValidateTime(ReportedTime time)
    {
        if (time is null)
        {
            throw new ArgumentException("The intake report is invalid.");
        }

        ReportedTimeRules.Validate(time);

        if (time.ZoneId is { Length: > 128 })
        {
            throw new ArgumentException("The intake report is invalid.");
        }
    }

    private static void ValidateLocation(LocationContext location)
    {
        if (location is null || location.CountryCode is not { Length: 2 } country ||
            country.Any(character => character is < 'A' or > 'Z') ||
            string.IsNullOrWhiteSpace(location.CatalogueVersion) ||
            location.CatalogueVersion.Length > 64 ||
            !Enum.IsDefined(location.Source) || !Enum.IsDefined(location.Precision) ||
            !ValidPlace(location.Region) || !ValidPlace(location.Area) || !ValidPlace(location.Locality))
        {
            throw new ArgumentException("The intake report is invalid.");
        }

        var hasLatitude = location.Latitude is not null;
        var hasLongitude = location.Longitude is not null;
        var accuracy = location.AccuracyMetres;
        if (hasLatitude != hasLongitude ||
            (hasLatitude && (!double.IsFinite(location.Latitude!.Value) || !double.IsFinite(location.Longitude!.Value) ||
                location.Latitude is < -90 or > 90 || location.Longitude is < -180 or > 180 ||
                accuracy is null || !double.IsFinite(accuracy.Value) || accuracy.Value <= 0 || accuracy.Value > 100_000 ||
                location.Precision == LocationPrecision.Unknown)) ||
            (!hasLatitude && location.AccuracyMetres is not null) ||
            (!hasLatitude && location.Source == LocationSource.Device) ||
            (!hasLatitude && location.Precision == LocationPrecision.Exact))
        {
            throw new ArgumentException("The intake report is invalid.");
        }
    }

    private static bool ValidPlace(OptionalPlace? place) =>
        place is not null && Enum.IsDefined(place.Presence) &&
        (place.ReportedName is null || place.ReportedName.Length <= 200) &&
        (place.NormalizedId is null || place.NormalizedId.Length <= 128) &&
        (place.Presence != PresenceState.Known ||
         !string.IsNullOrWhiteSpace(place.ReportedName) || !string.IsNullOrWhiteSpace(place.NormalizedId));

    private static void ValidateConfirmations(
        IntakeConfirmations confirmations,
        string countryCode,
        string catalogueVersion,
        bool audioPresent)
    {
        if (confirmations is null || !confirmations.ReportReviewConfirmed ||
            !confirmations.OccurrenceCountryConfirmed || !confirmations.ProcessingConfirmed ||
            !string.Equals(confirmations.OccurrenceCountryCode, countryCode, StringComparison.Ordinal) ||
            !string.Equals(confirmations.OccurrenceCountryCatalogueVersion, catalogueVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(confirmations.ProcessingPolicyReference) ||
            string.IsNullOrWhiteSpace(confirmations.ProcessingPolicyVersion))
        {
            throw new ArgumentException("The intake report is invalid.");
        }

        if (audioPresent && (confirmations.RecordingConfirmed is not true ||
                string.IsNullOrWhiteSpace(confirmations.RecordingPolicyReference) ||
                string.IsNullOrWhiteSpace(confirmations.RecordingPolicyVersion)) ||
            !audioPresent && (confirmations.RecordingConfirmed is not null ||
                confirmations.RecordingPolicyReference is not null || confirmations.RecordingPolicyVersion is not null))
        {
            throw new ArgumentException("The intake report is invalid.");
        }
    }
}

/// <summary>Application-configurable intake bounds with product-approved defaults.</summary>
public sealed record IntakeLimits(
    int MaximumNarrativeCharacters = 6_000,
    int MaximumAudioAttachments = 2,
    int MaximumAudioDurationSeconds = 120,
    long MaximumAudioBytes = 10 * 1024 * 1024,
    int MaximumSubmissionsPerCapabilityPerHour = 10,
    int MaximumObservations = 32,
    int MaximumObservationCharacters = 300)
{
    public const string SectionName = "Intake";

    public static IntakeLimits Default { get; } = new();

    public void Validate()
    {
        if (MaximumNarrativeCharacters is < 1 or > 20_000 ||
            MaximumAudioAttachments is < 0 or > 10 ||
            MaximumAudioDurationSeconds is < 1 or > 600 ||
            MaximumAudioBytes is < 1024 or > 100 * 1024 * 1024 ||
            MaximumSubmissionsPerCapabilityPerHour is < 1 or > 100 ||
            MaximumObservations is < 1 or > 100 ||
            MaximumObservationCharacters is < 1 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(IntakeLimits), "Intake bounds are outside supported limits.");
        }
    }
}
