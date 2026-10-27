using System.Collections.Immutable;

namespace SwiftAid.Client.LocalStore;

public enum ReportDraftKind
{
    Human,
    Animal
}

public enum DraftValueState
{
    Unknown,
    NotRecorded,
    NotApplicable,
    Absent,
    Known
}

public enum DraftReportSource
{
    ReporterNarrative,
    ReporterObservation,
    OriginalAudio,
    WorkerConfirmed
}

public enum DraftAnimalSubjectScope
{
    Individual,
    Herd,
    Flock,
    OtherGroup
}

/// <summary>A fact captured locally, with its explicit missingness and provenance retained.</summary>
public sealed record DraftFact<T>(DraftValueState State, T? Value, DraftProvenance Provenance)
{
    public DraftFact<T> Validate(string name)
    {
        if (!Enum.IsDefined(State)) throw new ArgumentOutOfRangeException(name, "Draft value state is not supported.");
        ArgumentNullException.ThrowIfNull(Provenance);
        Provenance.Validate(name);
        if (State == DraftValueState.Known && Value is null)
            throw new ArgumentException("A known draft fact requires a value.", name);
        if (State != DraftValueState.Known && Value is not null)
            throw new ArgumentException("Only a known draft fact may contain a value.", name);
        return this;
    }
}

public sealed record DraftProvenance(DraftReportSource Source, string StableReference)
{
    public DraftProvenance Validate(string name)
    {
        if (!Enum.IsDefined(Source)) throw new ArgumentOutOfRangeException(name, "Draft provenance source is not supported.");
        if (string.IsNullOrWhiteSpace(StableReference)) throw new ArgumentException("A stable provenance reference is required.", name);
        return this;
    }
}

/// <summary>Partial location and time capture. Null means not supplied; no values are inferred.</summary>
public sealed record DraftReportContext(
    DraftFact<DraftReportedTime?>? Occurrence,
    DateTimeOffset? CapturedAtUtc,
    string? CapturedOffset,
    string? CapturedZoneId,
    DraftFact<string?>? CountryCode,
    DraftFact<string?>? RegionReference,
    DraftFact<string?>? AreaReference,
    DraftFact<string?>? LocalityReference,
    DraftFact<double?>? Latitude,
    DraftFact<double?>? Longitude,
    DraftFact<double?>? AccuracyMetres,
    DraftFact<string?>? LocationSource,
    DraftFact<string?>? LocationPrecision)
{
    public void Validate()
    {
        Occurrence?.Validate(nameof(Occurrence));
        CountryCode?.Validate(nameof(CountryCode));
        RegionReference?.Validate(nameof(RegionReference));
        AreaReference?.Validate(nameof(AreaReference));
        LocalityReference?.Validate(nameof(LocalityReference));
        Latitude?.Validate(nameof(Latitude));
        Longitude?.Validate(nameof(Longitude));
        AccuracyMetres?.Validate(nameof(AccuracyMetres));
        LocationSource?.Validate(nameof(LocationSource));
        LocationPrecision?.Validate(nameof(LocationPrecision));
    }
}

public sealed record DraftReportedTime(
    DateTimeOffset? Value,
    DateOnly? LocalDate,
    DateOnly? RangeStart,
    DateOnly? RangeEnd,
    string? OriginalOffset,
    string? ZoneId,
    string Precision);

public sealed record DraftEvidence(
    string? OriginalNarrative,
    DraftProvenance? NarrativeProvenance,
    ImmutableArray<DraftObservation> Observations,
    ImmutableArray<DraftEvidenceReference> EvidenceReferences)
{
    public static DraftEvidence Empty => new(null, null, [], []);

    public void Validate()
    {
        if (OriginalNarrative is not null && string.IsNullOrWhiteSpace(OriginalNarrative))
            throw new ArgumentException("A supplied narrative cannot be blank.", nameof(OriginalNarrative));
        if ((OriginalNarrative is null) != (NarrativeProvenance is null))
            throw new ArgumentException("Narrative text and its provenance must be preserved together.");
        NarrativeProvenance?.Validate(nameof(NarrativeProvenance));
        if (Observations.IsDefault || EvidenceReferences.IsDefault)
            throw new ArgumentException("Draft evidence collections must be initialized.");
    }
}

public sealed record DraftObservation(string Text, DraftProvenance Provenance);

/// <summary>Opaque reference to original audio or another source asset; never contains asset bytes.</summary>
public sealed record DraftEvidenceReference(string Reference, DraftProvenance Provenance);

public sealed record DraftConfirmations(
    bool? ReportReviewConfirmed,
    string? OccurrenceCountryCode,
    string? OccurrenceCountryCatalogueVersion,
    bool? OccurrenceCountryConfirmed,
    bool? ProcessingConfirmed,
    string? ProcessingPolicyReference,
    string? ProcessingPolicyVersion,
    bool? RecordingConfirmed,
    string? RecordingPolicyReference,
    string? RecordingPolicyVersion);

public sealed record HumanReportDraft(
    DraftFact<string?>? PseudonymousSubjectReference,
    DraftFact<int?>? AgeYears,
    DraftFact<string?>? ReportedConcern);

public sealed record AnimalReportDraft(
    DraftAnimalSubjectScope? SubjectScope,
    DraftFact<string?>? Species,
    DraftFact<string?>? HoldingReference,
    DraftAnimalCount? AnimalsAtRisk,
    DraftAnimalCount? AnimalsAffected,
    DraftAnimalCount? AnimalsDead);

public sealed record DraftAnimalCount(
    DraftValueState State,
    int? Value,
    DraftProvenance Provenance,
    string? PopulationReference,
    DraftFact<DraftReportedTime?>? ObservationPeriod);

/// <summary>
/// A locally saved working copy. Its status is always Draft; it does not represent a queued,
/// submitted, accepted, or otherwise server-authoritative report.
/// </summary>
public sealed record ReportDraft(
    Guid DraftId,
    ReportDraftKind Kind,
    string WorkspaceId,
    DraftReportContext? Context,
    DraftEvidence Evidence,
    DraftConfirmations? Confirmations,
    HumanReportDraft? Human,
    AnimalReportDraft? Animal,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
    public const string Status = "Draft";
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string DraftState => Status;

    public ReportDraft Validate()
    {
        if (DraftId == Guid.Empty) throw new ArgumentException("A draft identifier is required.", nameof(DraftId));
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        if (SchemaVersion != CurrentSchemaVersion) throw new ArgumentOutOfRangeException(nameof(SchemaVersion));
        if (string.IsNullOrWhiteSpace(WorkspaceId)) throw new ArgumentException("A workspace is required.", nameof(WorkspaceId));
        if (WorkspaceId.Length > 200) throw new ArgumentException("Workspace identifier exceeds the local storage limit.", nameof(WorkspaceId));
        ArgumentNullException.ThrowIfNull(Evidence);
        Evidence.Validate();
        Context?.Validate();
        if (Kind == ReportDraftKind.Human && (Human is null || Animal is not null))
            throw new ArgumentException("A human draft must contain only human report fields.");
        if (Kind == ReportDraftKind.Animal && (Animal is null || Human is not null))
            throw new ArgumentException("An animal draft must contain only animal report fields.");

        if (CreatedAtUtc.Offset != TimeSpan.Zero || UpdatedAtUtc.Offset != TimeSpan.Zero || UpdatedAtUtc < CreatedAtUtc)
            throw new ArgumentException("Draft timestamps must be ordered UTC instants.");

        foreach (var observation in Evidence.Observations)
        {
            if (string.IsNullOrWhiteSpace(observation.Text)) throw new ArgumentException("An observation cannot be blank.");
            observation.Provenance.Validate(nameof(Evidence));
        }

        foreach (var reference in Evidence.EvidenceReferences)
        {
            if (string.IsNullOrWhiteSpace(reference.Reference)) throw new ArgumentException("An evidence reference cannot be blank.");
            reference.Provenance.Validate(nameof(Evidence));
        }

        if (Human is not null)
        {
            Human.PseudonymousSubjectReference?.Validate(nameof(Human));
            Human.AgeYears?.Validate(nameof(Human));
            Human.ReportedConcern?.Validate(nameof(Human));
            if (Human.AgeYears is { State: DraftValueState.Known, Value: < 0 })
                throw new ArgumentOutOfRangeException(nameof(Human), "A reported age cannot be negative.");
        }

        if (Animal is not null)
        {
            if (Animal.SubjectScope.HasValue && !Enum.IsDefined(Animal.SubjectScope.Value))
                throw new ArgumentOutOfRangeException(nameof(Animal));
            Animal.Species?.Validate(nameof(Animal));
            Animal.HoldingReference?.Validate(nameof(Animal));
            ValidateCount(Animal.AnimalsAtRisk);
            ValidateCount(Animal.AnimalsAffected);
            ValidateCount(Animal.AnimalsDead);
        }

        return this;
    }

    private static void ValidateCount(DraftAnimalCount? count)
    {
        if (count is null) return;
        if (!Enum.IsDefined(count.State)) throw new ArgumentOutOfRangeException(nameof(count));
        count.Provenance.Validate(nameof(count));
        if ((count.State == DraftValueState.Known) != count.Value.HasValue)
            throw new ArgumentException("Only a known animal count may contain a value.", nameof(count));
        if (count.Value < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count.ObservationPeriod is not null) count.ObservationPeriod.Validate(nameof(count));
    }
}
