using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Gateway.Cases;

/// <summary>
/// Server-side analytics client for Blazor Server: resolves the current staff
/// principal per call and projects the authorized aggregate queries into
/// labelled operational counts. Every metric states its scope, window, and
/// units; exports stay unavailable because private exports require records
/// policy, explicit field selection, audit, and safe destination handling.
/// </summary>
public sealed class AnalyticsGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider) : IAnalyticsClient
{
    private const string Capability = "analytics";

    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public async Task<UiCapabilityResult<Page<AnalyticsMetric>>> GetAsync(
        AnalyticsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Page is null || !query.Page.IsValid)
        {
            return Invalid<Page<AnalyticsMetric>>("Choose a valid page to browse analytics.");
        }

        if (!TryGetWindowDays(query.Period, out var days))
        {
            return Invalid<Page<AnalyticsMetric>>("Choose a supported period.");
        }

        if (!TryGetSector(query.WorkType, out var sector))
        {
            return Invalid<Page<AnalyticsMetric>>("Choose a supported work type.");
        }

        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return Denied<Page<AnalyticsMetric>>();
        }

        var aggregateQuery = _serviceProvider.GetService<IDashboardAggregateQuery>();
        var monitoringQuery = _serviceProvider.GetService<ICaseMonitoringQueueQuery>();
        if (aggregateQuery is null || monitoringQuery is null)
        {
            return Unavailable<Page<AnalyticsMetric>>("Analytics are not available right now.");
        }

        try
        {
            var window = TimeSpan.FromDays(days);
            var result = await aggregateQuery.GetAsync(actor, window, cancellationToken).ConfigureAwait(false);
            if (result.Access != DashboardAggregateAccess.Allowed)
            {
                return Unavailable<Page<AnalyticsMetric>>("Analytics are not available right now.");
            }

            var now = result.AsOfUtc;
            var sinceUtc = now.Add(-window);
            var rows = result.Rows
                .Where(row => sector is null || row.Sector == sector)
                .Where(row => string.IsNullOrWhiteSpace(query.Region) ||
                    string.Equals(row.CountryCode, query.Region, StringComparison.OrdinalIgnoreCase) ||
                    row.RegionIds.Any(region =>
                        string.Equals(region, query.Region, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var queue = await monitoringQuery.GetAssignedAsync(actor, 500, now, cancellationToken)
                .ConfigureAwait(false);
            var followUps = queue.Access == CaseQueueAccess.Allowed
                ? queue.Items.Where(item => item.Kind == MonitoringQueueKind.FollowUp).ToList()
                : null;

            var scope = $"owned readable cases, last {days} days";
            var metrics = new List<AnalyticsMetric>
            {
                new(
                    "active-owned-cases",
                    $"Active owned cases ({scope})",
                    rows.Count(row => row.Workflow != CaseWorkflow.Resolved),
                    "cases"),
                new(
                    "new-owned-cases",
                    $"New owned cases ({scope})",
                    rows.Count,
                    "cases"),
                new(
                    "closed-owned-cases",
                    $"Closed owned cases with recent activity ({scope})",
                    rows.Count(row => row.Workflow == CaseWorkflow.Resolved && row.UpdatedAtUtc >= sinceUtc),
                    "cases")
            };

            if (followUps is not null)
            {
                metrics.Add(new AnalyticsMetric(
                    "due-follow-ups",
                    "Due follow-ups assigned to you (all regions and sectors)",
                    followUps.Count,
                    "follow-ups"));
                metrics.Add(new AnalyticsMetric(
                    "overdue-follow-ups",
                    "Overdue follow-ups assigned to you (all regions and sectors)",
                    followUps.Count(item => item.IsOverdue),
                    "follow-ups"));
            }

            var skip = (query.Page.PageNumber - 1) * query.Page.PageSize;
            var items = metrics.Skip(skip).Take(query.Page.PageSize).ToList();
            var page = new Page<AnalyticsMetric>(
                items,
                query.Page.PageNumber,
                query.Page.PageSize,
                metrics.Count,
                skip + items.Count < metrics.Count);
            var message = result.Truncated ? "Showing totals for the most recent readable cases." : null;
            return new UiCapabilityResult<Page<AnalyticsMetric>>(
                UiCapabilityResultCode.Success, page, Metadata(available: true), message);
        }
        catch (Exception)
        {
            return Unavailable<Page<AnalyticsMetric>>("Analytics are not available right now.");
        }
    }

    public Task<UiCapabilityResult<AnalyticsExportPreview>> ExportPreviewAsync(
        AnalyticsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Unavailable<AnalyticsExportPreview>(
            "Exports require records policy, explicit field selection, audit, and safe destination handling."));
    }

    private static bool TryGetWindowDays(string? period, out int days)
    {
        days = 30;
        if (string.IsNullOrWhiteSpace(period))
        {
            return true;
        }

        if (string.Equals(period.Trim(), "30", StringComparison.Ordinal))
        {
            days = 30;
            return true;
        }

        if (string.Equals(period.Trim(), "90", StringComparison.Ordinal))
        {
            days = 90;
            return true;
        }

        return false;
    }

    private static bool TryGetSector(string? workType, out Sector? sector)
    {
        sector = null;
        if (string.IsNullOrWhiteSpace(workType))
        {
            return true;
        }

        if (string.Equals(workType.Trim(), "human", StringComparison.OrdinalIgnoreCase))
        {
            sector = Sector.HumanHealth;
            return true;
        }

        if (string.Equals(workType.Trim(), "animal", StringComparison.OrdinalIgnoreCase))
        {
            sector = Sector.AnimalHealth;
            return true;
        }

        return false;
    }

    private static UiCapabilityResult<T> Invalid<T>(string message) => new(
        UiCapabilityResultCode.InvalidRequest, default, Metadata(available: false), message);

    private static UiCapabilityResult<T> Denied<T>() => new(
        UiCapabilityResultCode.Denied, default, Metadata(available: false), "Sign in to view analytics.");

    private static UiCapabilityResult<T> Unavailable<T>(string message) => new(
        UiCapabilityResultCode.Unavailable, default, Metadata(available: false, message), message);

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
