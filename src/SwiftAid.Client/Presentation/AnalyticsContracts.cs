namespace SwiftAid.Client.Presentation;

public sealed record AnalyticsQuery(
    PageRequest Page,
    string Period = "30",
    string? Region = null,
    string? WorkType = null);

public sealed record AnalyticsMetric(
    string Id,
    string Label,
    int Value,
    string Unit,
    string? Change = null,
    string? Trend = null);

public sealed record AnalyticsExportPreview(
    string Title,
    int IncludedRows,
    IReadOnlyList<string> ColumnNames,
    string Notice);

public interface IAnalyticsClient
{
    Task<UiCapabilityResult<Page<AnalyticsMetric>>> GetAsync(
        AnalyticsQuery query,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<AnalyticsExportPreview>> ExportPreviewAsync(
        AnalyticsQuery query,
        CancellationToken cancellationToken = default);
}
