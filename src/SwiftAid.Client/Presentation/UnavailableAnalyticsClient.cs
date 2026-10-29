namespace SwiftAid.Client.Presentation;

/// <summary>Fail-closed analytics until an authorized live provider is available.</summary>
public sealed class UnavailableAnalyticsClient : IAnalyticsClient
{
    public Task<UiCapabilityResult<Page<AnalyticsMetric>>> GetAsync(
        AnalyticsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<Page<AnalyticsMetric>>("analytics"));
    }

    public Task<UiCapabilityResult<AnalyticsExportPreview>> ExportPreviewAsync(
        AnalyticsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<AnalyticsExportPreview>("analytics"));
    }
}
