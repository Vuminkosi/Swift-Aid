using SwiftAid.Contracts.Cases;
using SwiftAid.UI.Components.Theme;

namespace SwiftAid.UI.Components.Surfaces.Operations;

/// <summary>View state supplied by a host after it maps a typed service result.</summary>
public enum OperationsSurfaceState
{
    Loading,
    Empty,
    Populated,
    Offline,
    Unavailable,
    Denied,
    Error
}

public enum DashboardSectorView
{
    Human,
    Animal,
    OneHealth
}

public enum DashboardPeriodView
{
    Days30 = 30,
    Days90 = 90
}

public enum DashboardValueViewState
{
    Available,
    Suppressed,
    Unavailable
}

public enum DashboardKpiViewKind
{
    ActiveCases,
    AdministrativeHighPriorityCases,
    NewOperationalAlerts,
    ClosedInWindow
}

public enum DashboardCompletenessView
{
    Complete,
    Partial
}

public enum DashboardFreshnessView
{
    Current,
    Stale,
    Unknown
}

public enum DashboardWorkflowViewState
{
    Open,
    UnderReview,
    FollowUp,
    Escalated,
    Closed
}

public enum DashboardPriorityView
{
    Standard,
    High
}

public enum DashboardAlertViewKind
{
    NewCaseReceived,
    PriorityReviewRequested,
    FollowUpDue
}

public sealed record DashboardKpiView(
    DashboardKpiViewKind Kind,
    DashboardValueViewState State,
    long? Value,
    string Definition);

public sealed record DashboardCategoryView(
    DashboardSectorView Sector,
    string CategoryLabel,
    string ClassificationLabel,
    long? HumanCaseCount,
    long? AnimalReportCount,
    long? AnimalsAffected,
    long? Denominator,
    bool IsSuppressed);

public sealed record DashboardCaseView(
    string DisplayReference,
    DashboardSectorView SubjectSector,
    string ReportedConditionLabel,
    string ClassificationLabel,
    string RegionLabel,
    DashboardWorkflowViewState WorkflowState,
    DashboardPriorityView AdministrativePriority,
    DateTimeOffset ReportedAtUtc);

public sealed record DashboardAlertView(
    DashboardAlertViewKind Kind,
    DashboardSectorView Sector,
    string Label,
    string RegionLabel,
    DateTimeOffset OccurredAtUtc);

/// <summary>Dashboard data already translated by its host into presentation-only values.</summary>
public sealed record DashboardOverviewView(
    string AuthorizedScopeLabel,
    DashboardSectorView Sector,
    DashboardCompletenessView Completeness,
    DashboardFreshnessView Freshness,
    DateTimeOffset AsOfUtc,
    DateTimeOffset? FreshnessExpiresAtUtc,
    IReadOnlyList<DashboardKpiView> Kpis,
    IReadOnlyList<DashboardCategoryView> Categories,
    IReadOnlyList<DashboardCaseView> RecentCases,
    IReadOnlyList<DashboardAlertView> RecentAlerts);

/// <summary>Privacy-safe KPI row presented by the operations dashboard.</summary>
public sealed record OperationsMetricView(
    string Id,
    string Label,
    string Value,
    string Unit,
    string? Category = null,
    string? Trend = null);

/// <summary>Redacted, nonclinical entry for the operations activity feed.</summary>
public sealed record OperationsActivityView(
    string Id,
    string Title,
    string Summary,
    string Status,
    DateTimeOffset OccurredAtUtc);

/// <summary>Privacy-safe map marker supplied by an authorized aggregate service.</summary>
public sealed record OperationsMapPointView(
    string Id,
    string Label,
    string Category,
    string Status);

/// <summary>Capability display row already mapped and authorized by its host.</summary>
public sealed record OperationsListItemView(
    string Id,
    string Title,
    string Summary,
    string Category,
    string Value,
    string? Location = null,
    string? Status = null,
    DateTimeOffset? OccurredAtUtc = null);



public static class OperationsSurfaceStateExtensions
{
    public static SwiftAidViewState ToSwiftAidViewState(this OperationsSurfaceState state) => state switch
    {
        OperationsSurfaceState.Loading => SwiftAidViewState.Loading,
        OperationsSurfaceState.Empty => SwiftAidViewState.Empty,
        OperationsSurfaceState.Offline => SwiftAidViewState.Offline,
        OperationsSurfaceState.Denied => SwiftAidViewState.Denied,
        OperationsSurfaceState.Error => SwiftAidViewState.Error,
        OperationsSurfaceState.Unavailable => SwiftAidViewState.Information,
        _ => SwiftAidViewState.Populated
    };
}
