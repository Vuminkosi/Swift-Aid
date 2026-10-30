using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Gateway.Cases;

/// <summary>
/// Server-side dashboard client for Blazor Server: resolves the current staff
/// principal per call and delegates to the authorized aggregate query. Counts
/// cover only officer-owned cases the caller may still read; administrative
/// priority and operational alerts are not recorded on case rows and are
/// reported as unavailable rather than zero. Deployments without the case
/// command pipeline fail closed with the same unavailable outcome.
/// </summary>
public sealed class DashboardOverviewGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider,
    IIntakePublicMetadataProvider metadataProvider) : IDashboardOverviewClient
{
    private const string Capability = "dashboard-overview";
    private const int CategorySuppressionThreshold = 5;
    private const int RecentCaseLimit = 10;

    private static readonly TimeSpan FreshnessLifetime = TimeSpan.FromMinutes(5);

    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly IIntakePublicMetadataProvider _metadataProvider =
        metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));

    public async Task<UiCapabilityResult<DashboardOverview>> GetAsync(
        DashboardOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!query.IsValid)
        {
            return Invalid("Choose a supported period, sector, or region.");
        }

        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return Denied();
        }

        var aggregateQuery = _serviceProvider.GetService<IDashboardAggregateQuery>();
        if (aggregateQuery is null)
        {
            return Unavailable("The dashboard is not available right now.");
        }

        try
        {
            var result = await aggregateQuery.GetAsync(
                actor,
                TimeSpan.FromDays((int)query.Period),
                cancellationToken).ConfigureAwait(false);
            if (result.Access != DashboardAggregateAccess.Allowed)
            {
                return Unavailable("The dashboard is not available right now.");
            }

            var sinceUtc = result.AsOfUtc.Add(-TimeSpan.FromDays((int)query.Period));
            var overview = await ToOverviewAsync(query, result, sinceUtc, cancellationToken).ConfigureAwait(false);
            return overview.IsValid
                ? Success(overview, result.Truncated ? "Showing totals for the most recent readable cases." : null)
                : Unavailable("The dashboard could not be checked. Refresh to try again.");
        }
        catch (Exception)
        {
            return Unavailable("The dashboard is not available right now.");
        }
    }

    private async Task<DashboardOverview> ToOverviewAsync(
        DashboardOverviewQuery query,
        DashboardAggregateResult result,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        var rows = result.Rows
            .Where(row => SectorMatches(query.Sector, row.Sector))
            .Where(row => RegionMatches(query.RegionCode, row))
            .ToList();
        var labels = await LoadCountryLabelsAsync(cancellationToken).ConfigureAwait(false);
        var days = (int)query.Period;
        var scope = $"Owned {SectorLabel(query.Sector)} cases you may read, last {days} days" +
            (string.IsNullOrWhiteSpace(query.RegionCode) ? string.Empty : $" in {query.RegionCode.Trim()}");

        var active = rows.Where(row => row.Workflow != CaseWorkflow.Resolved).ToList();
        var closed = rows.Where(row => row.Workflow == CaseWorkflow.Resolved && row.UpdatedAtUtc >= sinceUtc).ToList();
        var hasAnimalCases = rows.Any(row => row.Sector == Sector.AnimalHealth);

        var kpis = new DashboardKpi[]
        {
            new(
                DashboardKpiKind.ActiveCases,
                DashboardValueState.Available,
                active.Count,
                $"Owned readable cases that are not resolved, created in the last {days} days."),
            new(
                DashboardKpiKind.AdministrativeHighPriorityCases,
                DashboardValueState.Unavailable,
                null,
                "Administrative priority is not recorded on case rows.",
                DashboardDataReason.NotCollected),
            new(
                DashboardKpiKind.NewOperationalAlerts,
                DashboardValueState.Unavailable,
                null,
                "Operational alerts are not recorded by this service.",
                DashboardDataReason.NotCollected),
            new(
                DashboardKpiKind.ClosedInWindow,
                DashboardValueState.Available,
                closed.Count,
                $"Owned readable cases resolved with activity in the last {days} days.")
        };

        var categories = BuildCategories(query.Sector, rows);
        var recent = rows
            .OrderByDescending(row => row.CreatedAtUtc)
            .Take(RecentCaseLimit)
            .Select(row => ToRecentCase(row, labels))
            .ToList();
        var asOfUtc = result.AsOfUtc;

        return new DashboardOverview(
            scope,
            query.Period,
            query.Sector,
            asOfUtc,
            result.Truncated || (query.Sector == DashboardOverviewSector.OneHealth && hasAnimalCases)
                ? DashboardDataCompleteness.Partial
                : DashboardDataCompleteness.Complete,
            DashboardFreshnessState.Current,
            asOfUtc.Add(FreshnessLifetime),
            kpis,
            categories,
            recent,
            Array.Empty<DashboardOperationalAlert>());
    }

    private static IReadOnlyList<DashboardCategoryAggregate> BuildCategories(
        DashboardOverviewSector sector,
        IReadOnlyList<DashboardAggregateCaseRow> rows)
    {
        // Animal group sizes are not recorded on case rows, so only human
        // classification rows can be emitted as valid aggregates.
        var human = rows.Where(row => row.Sector == Sector.HumanHealth).ToList();
        if (sector == DashboardOverviewSector.Animal || human.Count == 0)
        {
            return Array.Empty<DashboardCategoryAggregate>();
        }

        return human
            .GroupBy(row => row.Classification)
            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .Select(group => ToCategory(group.Key, group.ToList()))
            .ToList();
    }

    private static DashboardCategoryAggregate ToCategory(
        ClinicalClassification classification,
        IReadOnlyList<DashboardAggregateCaseRow> group)
    {
        var distinct = group.Select(row => row.CaseId).Distinct().Count();
        if (distinct < CategorySuppressionThreshold)
        {
            return new DashboardCategoryAggregate(
                DashboardOverviewSector.Human,
                "Human cases",
                classification.ToString(),
                null,
                null,
                null,
                null,
                true,
                DashboardDataReason.SmallGroupSuppression);
        }

        return new DashboardCategoryAggregate(
            DashboardOverviewSector.Human,
            "Human cases",
            classification.ToString(),
            distinct,
            null,
            null,
            null,
            false,
            null);
    }

    private static DashboardRecentCase ToRecentCase(
        DashboardAggregateCaseRow row,
        IReadOnlyDictionary<string, string> countryLabels)
    {
        var sector = row.Sector == Sector.AnimalHealth
            ? DashboardOverviewSector.Animal
            : DashboardOverviewSector.Human;
        var region = row.RegionIds.FirstOrDefault()
            ?? countryLabels.GetValueOrDefault(row.CountryCode)
            ?? row.CountryCode;
        if (region.Length > 100)
        {
            region = row.CountryCode;
        }
        return new DashboardRecentCase(
            row.CaseId.ToString("D"),
            "Case " + row.CaseId.ToString("N")[..8],
            sector,
            row.Sector == Sector.AnimalHealth ? "Animal report" : "Human case report",
            row.Classification.ToString(),
            region,
            ToWorkflowState(row.Workflow),
            DashboardAdministrativePriority.Standard,
            row.CreatedAtUtc);
    }

    private static DashboardWorkflowState ToWorkflowState(CaseWorkflow workflow) => workflow switch
    {
        CaseWorkflow.UnderReview => DashboardWorkflowState.UnderReview,
        CaseWorkflow.AwaitingFollowUp => DashboardWorkflowState.FollowUp,
        CaseWorkflow.Resolved => DashboardWorkflowState.Closed,
        _ => DashboardWorkflowState.Open
    };

    private static bool SectorMatches(DashboardOverviewSector sector, Sector row) => sector switch
    {
        DashboardOverviewSector.Human => row == Sector.HumanHealth,
        DashboardOverviewSector.Animal => row == Sector.AnimalHealth,
        _ => row is Sector.HumanHealth or Sector.AnimalHealth
    };

    private static string SectorLabel(DashboardOverviewSector sector) => sector switch
    {
        DashboardOverviewSector.Human => "human",
        DashboardOverviewSector.Animal => "animal",
        _ => "human and animal"
    };

    private static bool RegionMatches(string? regionCode, DashboardAggregateCaseRow row)
    {
        if (string.IsNullOrWhiteSpace(regionCode))
        {
            return true;
        }

        return string.Equals(row.CountryCode, regionCode, StringComparison.OrdinalIgnoreCase) ||
            row.RegionIds.Any(region => string.Equals(region, regionCode, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadCountryLabelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var catalogue = await _metadataProvider.GetCountriesAsync(cancellationToken).ConfigureAwait(false);
            return catalogue.Countries.ToDictionary(
                country => country.Code, country => country.Label, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static UiCapabilityResult<DashboardOverview> Success(DashboardOverview overview, string? message) => new(
        UiCapabilityResultCode.Success,
        overview,
        Metadata(available: true),
        message);

    private static UiCapabilityResult<DashboardOverview> Invalid(string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        Metadata(available: false),
        message);

    private static UiCapabilityResult<DashboardOverview> Denied() => new(
        UiCapabilityResultCode.Denied,
        default,
        Metadata(available: false),
        "Sign in to view the dashboard.");

    private static UiCapabilityResult<DashboardOverview> Unavailable(string message) => new(
        UiCapabilityResultCode.Unavailable,
        default,
        Metadata(available: false, message),
        message);

    private static UiCapabilityMetadata Metadata(bool available, string? notice = null) => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        available,
        notice);

    private async Task<ActorContext?> GetActorAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = state.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        try
        {
            return ActorContext.FromValidatedPrincipal(principal);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
