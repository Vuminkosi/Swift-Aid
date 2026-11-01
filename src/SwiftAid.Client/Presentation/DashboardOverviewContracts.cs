namespace SwiftAid.Client.Presentation;

public enum DashboardOverviewSector
{
    Human,
    Animal,
    OneHealth
}

public enum DashboardOverviewPeriod
{
    Days30 = 30,
    Days90 = 90
}

public enum DashboardValueState
{
    Available,
    Suppressed,
    Unavailable
}

public enum DashboardKpiKind
{
    ActiveCases,
    AdministrativeHighPriorityCases,
    NewOperationalAlerts,
    ClosedInWindow
}

public enum DashboardDataCompleteness
{
    Complete,
    Partial
}

public enum DashboardFreshnessState
{
    Current,
    Stale,
    Unknown
}

public enum DashboardDataReason
{
    SmallGroupSuppression,
    SourceUnavailable,
    NotCollected
}

public enum DashboardWorkflowState
{
    Open,
    UnderReview,
    FollowUp,
    Escalated,
    Closed
}

public enum DashboardAdministrativePriority
{
    Standard,
    High
}

public enum DashboardOperationalAlertKind
{
    NewCaseReceived,
    PriorityReviewRequested,
    FollowUpDue
}

public enum DashboardOperationalAlertLabel
{
    NewCaseReportReceived,
    AdministrativePriorityReviewRequested,
    ScheduledFollowUpDue
}

/// <summary>A bounded dashboard query. Authorization and data scope are derived by the service.</summary>
public sealed record DashboardOverviewQuery(
    DashboardOverviewPeriod Period = DashboardOverviewPeriod.Days30,
    string? RegionCode = null,
    DashboardOverviewSector Sector = DashboardOverviewSector.Human)
{
    public bool IsValid => Enum.IsDefined(Period) && Enum.IsDefined(Sector) &&
        (string.IsNullOrWhiteSpace(RegionCode) ||
         (RegionCode.Length is >= 2 and <= 24 && RegionCode.All(IsRegionCodeCharacter)));

    private static bool IsRegionCodeCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value == '-';
}

/// <summary>One count with explicit completeness semantics; unavailable or withheld values are never zero.</summary>
public sealed record DashboardKpi(
    DashboardKpiKind Kind,
    DashboardValueState State,
    long? Value,
    string Definition,
    DashboardDataReason? Reason = null)
{
    public bool IsValid => Enum.IsDefined(Kind) && Enum.IsDefined(State) &&
        !string.IsNullOrWhiteSpace(Definition) && Definition.Length <= 240 &&
        (State == DashboardValueState.Available
            ? Value is >= 0 && Reason is null
            : Value is null && Reason.HasValue && Enum.IsDefined(Reason.Value) &&
                (State != DashboardValueState.Suppressed || Reason == DashboardDataReason.SmallGroupSuppression));
}

/// <summary>Sector-specific category totals; human cases and animal reports are kept separate.</summary>
public sealed record DashboardCategoryAggregate(
    DashboardOverviewSector Sector,
    string CategoryLabel,
    string ClassificationLabel,
    long? HumanCaseCount,
    long? AnimalReportCount,
    long? AnimalsAffected,
    long? Denominator,
    bool IsSuppressed,
    DashboardDataReason? Reason)
{
    public bool IsValid
    {
        get
        {
            if (!Enum.IsDefined(Sector) || string.IsNullOrWhiteSpace(CategoryLabel) || CategoryLabel.Length > 100 ||
                string.IsNullOrWhiteSpace(ClassificationLabel) || ClassificationLabel.Length > 100 ||
                HumanCaseCount is < 0 || AnimalReportCount is < 0 || AnimalsAffected is < 0 || Denominator is < 0)
                return false;

            if (IsSuppressed)
                return HumanCaseCount is null && AnimalReportCount is null && AnimalsAffected is null && Denominator is null &&
                    Reason == DashboardDataReason.SmallGroupSuppression;

            if (Reason.HasValue) return false;
            return Sector switch
            {
                DashboardOverviewSector.Human => HumanCaseCount.HasValue && AnimalReportCount is null && AnimalsAffected is null,
                DashboardOverviewSector.Animal => HumanCaseCount is null && AnimalReportCount.HasValue && AnimalsAffected.HasValue,
                _ => false
            };
        }
    }
}

/// <summary>Minimum-necessary row projection. CaseId is opaque and must be authorized again on navigation.</summary>
public sealed record DashboardRecentCase(
    string CaseId,
    string DisplayReference,
    DashboardOverviewSector SubjectSector,
    string ReportedConditionLabel,
    string ClassificationLabel,
    string RegionLabel,
    DashboardWorkflowState WorkflowState,
    DashboardAdministrativePriority AdministrativePriority,
    DateTimeOffset ReportedAtUtc,
    string? LocationHref = null,
    string? ObservationPreview = null)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(CaseId) && CaseId.Length <= 128 &&
        !string.IsNullOrWhiteSpace(DisplayReference) && DisplayReference.Length <= 64 &&
        SubjectSector is DashboardOverviewSector.Human or DashboardOverviewSector.Animal &&
        !string.IsNullOrWhiteSpace(ReportedConditionLabel) && ReportedConditionLabel.Length <= 100 &&
        !string.IsNullOrWhiteSpace(ClassificationLabel) && ClassificationLabel.Length <= 100 &&
        !string.IsNullOrWhiteSpace(RegionLabel) && RegionLabel.Length <= 100 &&
        Enum.IsDefined(WorkflowState) && Enum.IsDefined(AdministrativePriority) && ReportedAtUtc != default;
}

/// <summary>An administrative event only; it does not represent a clinical signal or inferred outbreak.</summary>
public sealed record DashboardOperationalAlert(
    string AlertId,
    DashboardOperationalAlertKind Kind,
    DashboardOverviewSector Sector,
    string RegionLabel,
    DateTimeOffset OccurredAtUtc)
{
    public DashboardOperationalAlertLabel Label => Kind switch
    {
        DashboardOperationalAlertKind.NewCaseReceived => DashboardOperationalAlertLabel.NewCaseReportReceived,
        DashboardOperationalAlertKind.PriorityReviewRequested => DashboardOperationalAlertLabel.AdministrativePriorityReviewRequested,
        DashboardOperationalAlertKind.FollowUpDue => DashboardOperationalAlertLabel.ScheduledFollowUpDue,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };

    public bool IsValid => !string.IsNullOrWhiteSpace(AlertId) && AlertId.Length <= 128 && Enum.IsDefined(Kind) &&
        Sector is DashboardOverviewSector.Human or DashboardOverviewSector.Animal &&
        !string.IsNullOrWhiteSpace(RegionLabel) && RegionLabel.Length <= 100 && OccurredAtUtc != default;
}

public sealed record DashboardOverview(
    string AuthorizedScopeLabel,
    DashboardOverviewPeriod Period,
    DashboardOverviewSector Sector,
    DateTimeOffset AsOfUtc,
    DashboardDataCompleteness Completeness,
    DashboardFreshnessState FreshnessState,
    DateTimeOffset? FreshnessExpiresAtUtc,
    IReadOnlyList<DashboardKpi> Kpis,
    IReadOnlyList<DashboardCategoryAggregate> Categories,
    IReadOnlyList<DashboardRecentCase> RecentCases,
    IReadOnlyList<DashboardOperationalAlert> RecentOperationalAlerts)
{
    /// <summary>Evaluates expiration against the consumer's current clock rather than response time.</summary>
    public DashboardFreshnessState EvaluateFreshness(DateTimeOffset evaluatedAtUtc)
    {
        if (evaluatedAtUtc == default)
            throw new ArgumentOutOfRangeException(nameof(evaluatedAtUtc));

        if (FreshnessState is DashboardFreshnessState.Unknown or DashboardFreshnessState.Stale)
            return FreshnessState;
        if (FreshnessState != DashboardFreshnessState.Current || !FreshnessExpiresAtUtc.HasValue)
            return DashboardFreshnessState.Unknown;

        return FreshnessExpiresAtUtc.Value <= evaluatedAtUtc
            ? DashboardFreshnessState.Stale
            : DashboardFreshnessState.Current;
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(AuthorizedScopeLabel) && AuthorizedScopeLabel.Length <= 160 &&
        Enum.IsDefined(Period) && Enum.IsDefined(Sector) && AsOfUtc != default && Enum.IsDefined(Completeness) &&
        Enum.IsDefined(FreshnessState) && (FreshnessState switch
        {
            DashboardFreshnessState.Current => FreshnessExpiresAtUtc.HasValue && FreshnessExpiresAtUtc > AsOfUtc,
            DashboardFreshnessState.Stale => FreshnessExpiresAtUtc.HasValue && FreshnessExpiresAtUtc <= AsOfUtc,
            DashboardFreshnessState.Unknown => FreshnessExpiresAtUtc is null,
            _ => false
        }) &&
        Kpis is not null && Categories is not null && RecentCases is not null && RecentOperationalAlerts is not null &&
        Kpis.Count == 4 && Enum.GetValues<DashboardKpiKind>().All(kind => Kpis.Count(kpi => kpi.Kind == kind) == 1) &&
        Kpis.All(kpi => kpi.IsValid) && Categories.All(category => category.IsValid) &&
        HasUniqueCategoryKeys &&
        (Sector == DashboardOverviewSector.OneHealth || Categories.All(category => category.Sector == Sector)) &&
        Categories.All(category => category.Sector is DashboardOverviewSector.Human or DashboardOverviewSector.Animal) &&
        RecentCases.All(item => item.IsValid && (Sector == DashboardOverviewSector.OneHealth || item.SubjectSector == Sector)) &&
        RecentOperationalAlerts.All(item => item.IsValid) &&
        (Sector == DashboardOverviewSector.OneHealth || RecentOperationalAlerts.All(alert => alert.Sector == Sector));

    private bool HasUniqueCategoryKeys => Categories
        .GroupBy(category => category.Sector)
        .All(sectorGroup => sectorGroup
            .GroupBy(category => category.CategoryLabel, StringComparer.OrdinalIgnoreCase)
            .All(labelGroup => labelGroup
                .GroupBy(category => category.ClassificationLabel, StringComparer.OrdinalIgnoreCase)
                .All(classificationGroup => classificationGroup.Count() == 1)));
}

public interface IDashboardOverviewClient
{
    Task<UiCapabilityResult<DashboardOverview>> GetAsync(
        DashboardOverviewQuery query,
        CancellationToken cancellationToken = default);
}
