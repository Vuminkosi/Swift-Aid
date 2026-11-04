using SwiftAid.Client.Presentation;

namespace SwiftAid.Ui.TestHost;

public sealed class AnalyticsFixture : IAnalyticsClient
{
    public Task<UiCapabilityResult<Page<AnalyticsMetric>>> GetAsync(AnalyticsQuery query, CancellationToken cancellationToken = default)
    {
        var label = query.WorkType switch
        {
            "human" => "Synthetic human cases",
            "animal" => "Synthetic animal cases",
            _ => "Synthetic readable cases"
        };
        var metric = new AnalyticsMetric("fixture", label, 7, "cases");
        var page = new Page<AnalyticsMetric>([metric], query.Page.PageNumber, query.Page.PageSize, 1, false);
        return Task.FromResult(new UiCapabilityResult<Page<AnalyticsMetric>>(UiCapabilityResultCode.Success, page,
            UiCapabilityResults.SyntheticMetadata("analytics", "Fixture"), "Showing totals for the most recent readable cases."));
    }

    public Task<UiCapabilityResult<AnalyticsExportPreview>> ExportPreviewAsync(AnalyticsQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(UiCapabilityResults.Unavailable<AnalyticsExportPreview>("analytics-export"));
}
