namespace SwiftAid.Client.Presentation;

/// <summary>Fail-closed dashboard projection until an authorized live provider is available.</summary>
public sealed class UnavailableDashboardOverviewClient : IDashboardOverviewClient
{
    public Task<UiCapabilityResult<DashboardOverview>> GetAsync(
        DashboardOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!query.IsValid)
        {
            return Task.FromResult(new UiCapabilityResult<DashboardOverview>(
                UiCapabilityResultCode.InvalidRequest,
                null,
                UiCapabilityResults.UnavailableMetadata("dashboard-overview", "Choose a supported period, sector, or region."),
                "Choose a supported period, sector, or region."));
        }

        return Task.FromResult(UiCapabilityResults.Unavailable<DashboardOverview>("dashboard-overview"));
    }
}
