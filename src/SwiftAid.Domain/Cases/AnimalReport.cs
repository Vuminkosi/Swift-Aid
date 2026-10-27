using SwiftAid.Contracts.Geography;

namespace SwiftAid.Domain.Cases;

public enum AnimalSubjectScope
{
    Individual,
    Herd,
    Flock,
    OtherGroup
}

public enum ReportValidationIssueKind
{
    CountRelationshipNeedsCorrection
}

/// <summary>A factual count tied to its stated population and observation period.</summary>
public sealed record AnimalCount
{
    public AnimalCount(
        ReportedValue<int> Value,
        ReportProvenance Provenance,
        string? PopulationReference,
        ReportedFact<ReportedTime>? ObservationPeriod)
    {
        this.Value = Value;
        this.Provenance = Provenance;
        this.PopulationReference = PopulationReference;
        this.ObservationPeriod = ObservationPeriod;
    }

    public ReportedValue<int> Value { get; init; }
    public ReportProvenance Provenance { get; init; }
    public string? PopulationReference { get; init; }
    private ReportedFact<ReportedTime>? _observationPeriod;
    public ReportedFact<ReportedTime>? ObservationPeriod
    {
        get => _observationPeriod;
        init => _observationPeriod = value is null ? null : ReportedTimeRules.Normalize(value);
    }

    public void Deconstruct(
        out ReportedValue<int> Value,
        out ReportProvenance Provenance,
        out string? PopulationReference,
        out ReportedFact<ReportedTime>? ObservationPeriod)
    {
        Value = this.Value;
        Provenance = this.Provenance;
        PopulationReference = this.PopulationReference;
        ObservationPeriod = this.ObservationPeriod;
    }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Provenance);
        Provenance.Validate();
        ObservationPeriod?.Validate();

        if (Value.HasValue && Value.Value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Value), "An animal count cannot be negative.");
        }

        if (Value.HasValue && string.IsNullOrWhiteSpace(PopulationReference) && PopulationReference is not null)
        {
            throw new ArgumentException("A known population reference cannot be blank.", nameof(PopulationReference));
        }
    }

    public static AnimalCount Known(int count, ReportProvenance provenance, string? populationReference = null, ReportedFact<ReportedTime>? observationPeriod = null) =>
        new(ReportedValue<int>.Known(count), provenance, populationReference, observationPeriod);

    public static AnimalCount Unknown(ReportProvenance provenance) => new(ReportedValue<int>.Unknown, provenance, null, null);
    public static AnimalCount NotRecorded(ReportProvenance provenance) => new(ReportedValue<int>.NotRecorded, provenance, null, null);
    public static AnimalCount NotApplicable(ReportProvenance provenance) => new(ReportedValue<int>.NotApplicable, provenance, null, null);
    public static AnimalCount Absent(ReportProvenance provenance) => new(ReportedValue<int>.Absent, provenance, null, null);
}

public sealed record ReportValidationIssue(ReportValidationIssueKind Kind, string Field, string Message);

/// <summary>One animal event report with group and animal denominators kept distinct.</summary>
public sealed class AnimalReport
{
    private AnimalReport(
        Guid reportId,
        ReportContext context,
        AnimalSubjectScope subjectScope,
        ReportedFact<string> species,
        ReportedFact<string> holdingReference,
        ReportEvidence evidence,
        AnimalCount animalsAtRisk,
        AnimalCount animalsAffected,
        AnimalCount animalsDead)
    {
        ReportId = reportId;
        Context = context;
        SubjectScope = subjectScope;
        Species = species;
        HoldingReference = holdingReference;
        Evidence = evidence;
        AnimalsAtRisk = animalsAtRisk;
        AnimalsAffected = animalsAffected;
        AnimalsDead = animalsDead;
    }

    public Guid ReportId { get; }
    public ReportContext Context { get; }
    public AnimalSubjectScope SubjectScope { get; }
    public ReportedFact<string> Species { get; }
    public ReportedFact<string> HoldingReference { get; }
    public ReportEvidence Evidence { get; }
    public AnimalCount AnimalsAtRisk { get; }
    public AnimalCount AnimalsAffected { get; }
    public AnimalCount AnimalsDead { get; }

    public static AnimalReport Create(
        Guid reportId,
        ReportContext context,
        AnimalSubjectScope subjectScope,
        ReportedFact<string> species,
        ReportedFact<string> holdingReference,
        ReportEvidence evidence,
        AnimalCount animalsAtRisk,
        AnimalCount animalsAffected,
        AnimalCount animalsDead)
    {
        if (reportId == Guid.Empty) throw new ArgumentException("A report identifier is required.", nameof(reportId));
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(species);
        ArgumentNullException.ThrowIfNull(holdingReference);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(animalsAtRisk);
        ArgumentNullException.ThrowIfNull(animalsAffected);
        ArgumentNullException.ThrowIfNull(animalsDead);
        if (!Enum.IsDefined(subjectScope)) throw new ArgumentOutOfRangeException(nameof(subjectScope));

        context.Validate();
        species.Validate();
        holdingReference.Validate();
        evidence.Validate();
        animalsAtRisk.Validate();
        animalsAffected.Validate();
        animalsDead.Validate();

        return new AnimalReport(reportId, context, subjectScope, species, holdingReference, evidence, animalsAtRisk, animalsAffected, animalsDead);
    }

    /// <summary>Returns correction requests for contradictory counts with matching known denominators.</summary>
    public IReadOnlyList<ReportValidationIssue> Validate()
    {
        var issues = new List<ReportValidationIssue>();

        if (Comparable(AnimalsAtRisk, AnimalsAffected) && AnimalsAffected.Value.Value > AnimalsAtRisk.Value.Value)
        {
            issues.Add(new ReportValidationIssue(
                ReportValidationIssueKind.CountRelationshipNeedsCorrection,
                "reported-affected",
                "Reported affected count exceeds the at-risk count for the same population and observation period."));
        }

        if (Comparable(AnimalsAffected, AnimalsDead) && AnimalsDead.Value.Value > AnimalsAffected.Value.Value)
        {
            issues.Add(new ReportValidationIssue(
                ReportValidationIssueKind.CountRelationshipNeedsCorrection,
                "reported-dead",
                "Reported dead count exceeds the affected count for the same population and observation period."));
        }

        return Array.AsReadOnly(issues.ToArray());
    }

    private static bool Comparable(AnimalCount left, AnimalCount right) =>
        left.Value.HasValue &&
        right.Value.HasValue &&
        !string.IsNullOrWhiteSpace(left.PopulationReference) &&
        string.Equals(left.PopulationReference, right.PopulationReference, StringComparison.Ordinal) &&
        left.ObservationPeriod is { Value.HasValue: true } leftPeriod &&
        right.ObservationPeriod is { Value.HasValue: true } rightPeriod &&
        leftPeriod.Value.Value == rightPeriod.Value.Value;
}
