using SwiftAid.Contracts.Geography;

namespace SwiftAid.Domain.Cases;

public enum ReportSourceKind
{
    ReporterNarrative,
    ReporterObservation,
    OriginalAudio,
    WorkerConfirmed
}

public sealed record ReportProvenance(ReportSourceKind Source, string StableReference)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Source)) throw new ArgumentOutOfRangeException(nameof(Source));
        if (string.IsNullOrWhiteSpace(StableReference))
        {
            throw new ArgumentException("A stable evidence reference is required for report provenance.", nameof(StableReference));
        }
    }
}

/// <summary>A reported value with its missingness state and source preserved.</summary>
public sealed record ReportedFact<T>(ReportedValue<T> Value, ReportProvenance Provenance)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Provenance);
        Provenance.Validate();
    }

    public static ReportedFact<T> Known(T value, ReportProvenance provenance) => new(ReportedValue<T>.Known(value), provenance);
    public static ReportedFact<T> Unknown(ReportProvenance provenance) => new(ReportedValue<T>.Unknown, provenance);
    public static ReportedFact<T> NotRecorded(ReportProvenance provenance) => new(ReportedValue<T>.NotRecorded, provenance);
    public static ReportedFact<T> NotApplicable(ReportProvenance provenance) => new(ReportedValue<T>.NotApplicable, provenance);
    public static ReportedFact<T> Absent(ReportProvenance provenance) => new(ReportedValue<T>.Absent, provenance);
}

public sealed record ReportedText(string Text, ReportProvenance Provenance)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Text)) throw new ArgumentException("Reported text cannot be empty.", nameof(Text));
        ArgumentNullException.ThrowIfNull(Provenance);
        Provenance.Validate();
    }
}

/// <summary>An opaque reference to original audio; it does not imply upload or transcription.</summary>
public sealed record ReportEvidenceReference(string Reference, ReportProvenance Provenance)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Reference)) throw new ArgumentException("An evidence reference is required.", nameof(Reference));
        ArgumentNullException.ThrowIfNull(Provenance);
        Provenance.Validate();
        if (Provenance.Source != ReportSourceKind.OriginalAudio)
        {
            throw new ArgumentException("An original-audio reference must retain original-audio provenance.", nameof(Provenance));
        }
    }
}

/// <summary>Protected, attributed source evidence retained with a report.</summary>
public sealed class ReportEvidence
{
    private readonly IReadOnlyList<ReportedText> _observations;

    public ReportEvidence(ReportedText? narrative, IEnumerable<ReportedText> observations, ReportEvidenceReference? originalAudio)
    {
        ArgumentNullException.ThrowIfNull(observations);
        Narrative = narrative;
        _observations = Array.AsReadOnly(observations.ToArray());
        OriginalAudio = originalAudio;
    }

    public ReportedText? Narrative { get; }
    public IReadOnlyList<ReportedText> Observations => _observations;
    public ReportEvidenceReference? OriginalAudio { get; }
    public static ReportEvidence Empty => new(null, [], null);

    internal void Validate()
    {
        Narrative?.Validate();
        foreach (var observation in _observations) observation.Validate();
        OriginalAudio?.Validate();
        if (Narrative is null && _observations.Count == 0 && OriginalAudio is null)
        {
            throw new ArgumentException("A report requires an observation, narrative, or original-audio reference.");
        }
    }
}

/// <summary>Occurrence, capture and location remain separate reporter-supplied facts.</summary>
public sealed record ReportContext
{
    public ReportContext(
        ReportedFact<ReportedTime> occurrence,
        DateTimeOffset? capturedAtUtc,
        string? capturedOffset,
        string? capturedZoneId,
        ReportedTimePrecision capturePrecision,
        LocationContext location,
        ReportedFact<ReportedTime>? onset = null)
    {
        Occurrence = ReportedTimeRules.Normalize(occurrence);
        Onset = onset is null ? null : ReportedTimeRules.Normalize(onset);
        CapturedAtUtc = capturedAtUtc?.ToUniversalTime();
        CapturedOffset = capturedOffset;
        CapturedZoneId = capturedZoneId;
        CapturePrecision = capturePrecision;
        Location = location;
    }

    public ReportedFact<ReportedTime> Occurrence { get; }
    /// <summary>Separately reported symptom or event onset; null means this older report did not capture it.</summary>
    public ReportedFact<ReportedTime>? Onset { get; }
    public DateTimeOffset? CapturedAtUtc { get; }
    public string? CapturedOffset { get; }
    public string? CapturedZoneId { get; }
    public ReportedTimePrecision CapturePrecision { get; }
    public LocationContext Location { get; }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Occurrence);
        Occurrence.Validate();
        Onset?.Validate();
        if (!Enum.IsDefined(CapturePrecision)) throw new ArgumentOutOfRangeException(nameof(CapturePrecision));
        if (Occurrence.Value.HasValue) ReportedTimeRules.Validate(Occurrence.Value.Value);
        if (Onset?.Value.HasValue == true) ReportedTimeRules.Validate(Onset.Value.Value);

        ArgumentNullException.ThrowIfNull(Location);
        if (!double.IsFinite(Location.AccuracyMetres.GetValueOrDefault()) && Location.AccuracyMetres.HasValue)
        {
            throw new ArgumentOutOfRangeException(nameof(Location), "Location accuracy must be finite.");
        }

        if (Location.AccuracyMetres < 0) throw new ArgumentOutOfRangeException(nameof(Location), "Location accuracy cannot be negative.");
        if ((Location.Latitude.HasValue && !double.IsFinite(Location.Latitude.Value)) ||
            (Location.Longitude.HasValue && !double.IsFinite(Location.Longitude.Value)))
        {
            throw new ArgumentOutOfRangeException(nameof(Location), "Reported coordinates must be finite.");
        }

        if (Location.Latitude is < -90 or > 90 || Location.Longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(Location), "Reported coordinates are outside their valid ranges.");
        }

        ReportedTimeRules.ValidateOffsetAndZone(CapturedAtUtc, CapturedOffset, CapturedZoneId, nameof(CapturedOffset));
    }
}

/// <summary>One human subject report; narratives are retained as evidence and are never split automatically.</summary>
public sealed class HumanReport
{
    private HumanReport(
        Guid reportId,
        ReportContext context,
        ReportedFact<string> pseudonymousSubjectReference,
        ReportedFact<int> ageYears,
        ReportedFact<string> reportedConcern,
        ReportEvidence evidence)
    {
        ReportId = reportId;
        Context = context;
        PseudonymousSubjectReference = pseudonymousSubjectReference;
        AgeYears = ageYears;
        ReportedConcern = reportedConcern;
        Evidence = evidence;
    }

    public Guid ReportId { get; }
    public ReportContext Context { get; }
    /// <summary>An opaque pseudonymous reference; no reference format is prescribed by this domain model.</summary>
    public ReportedFact<string> PseudonymousSubjectReference { get; }
    public ReportedFact<int> AgeYears { get; }
    /// <summary>Text attributed to the reporter; it is not a clinical classification.</summary>
    public ReportedFact<string> ReportedConcern { get; }
    public ReportEvidence Evidence { get; }

    public static HumanReport Create(
        Guid reportId,
        ReportContext context,
        ReportedFact<string> pseudonymousSubjectReference,
        ReportedFact<int> ageYears,
        ReportedFact<string> reportedConcern,
        ReportEvidence evidence)
    {
        if (reportId == Guid.Empty) throw new ArgumentException("A report identifier is required.", nameof(reportId));
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pseudonymousSubjectReference);
        ArgumentNullException.ThrowIfNull(ageYears);
        ArgumentNullException.ThrowIfNull(reportedConcern);
        ArgumentNullException.ThrowIfNull(evidence);

        context.Validate();
        pseudonymousSubjectReference.Validate();
        ageYears.Validate();
        reportedConcern.Validate();
        evidence.Validate();

        if (ageYears.Value.HasValue && ageYears.Value.Value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ageYears), "A reported age cannot be negative.");
        }

        return new HumanReport(reportId, context, pseudonymousSubjectReference, ageYears, reportedConcern, evidence);
    }
}
