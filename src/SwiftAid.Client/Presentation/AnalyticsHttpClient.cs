using System.Net;
using System.Net.Http.Json;

namespace SwiftAid.Client.Presentation;

public sealed class AnalyticsHttpClient(HttpClient httpClient) : IAnalyticsClient
{
    public async Task<UiCapabilityResult<Page<AnalyticsMetric>>> GetAsync(AnalyticsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (httpClient.BaseAddress is null || query.Page is not { IsValid: true, PageNumber: <= 100_000 } ||
            query.Region?.Length > 128 || query.WorkType?.Length > 32 || query.Period?.Length > 8)
            return Result(UiCapabilityResultCode.InvalidRequest);
        var address = $"api/v1/analytics/work-summary?page={query.Page.PageNumber}&pageSize={query.Page.PageSize}&period={Uri.EscapeDataString(query.Period ?? "30")}";
        if (!string.IsNullOrWhiteSpace(query.Region)) address += "&region=" + Uri.EscapeDataString(query.Region);
        if (!string.IsNullOrWhiteSpace(query.WorkType)) address += "&workType=" + Uri.EscapeDataString(query.WorkType);
        try
        {
            using var response = await httpClient.GetAsync(address, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Result(UiCapabilityResultCode.Denied);
            if (!response.IsSuccessStatusCode) return Result(UiCapabilityResultCode.Unavailable);
            var result = await response.Content.ReadFromJsonAsync<UiCapabilityResult<Page<AnalyticsMetric>>>(cancellationToken: cancellationToken).ConfigureAwait(false);
            var page = result?.Value;
            if (result?.Code != UiCapabilityResultCode.Success || result.Metadata is not { IsAvailable: true, Provenance: UiCapabilityProvenance.LiveService } ||
                page?.Items is null || page.PageNumber != query.Page.PageNumber || page.PageSize != query.Page.PageSize ||
                page.Items.Count > query.Page.PageSize || page.TotalCount < page.Items.Count ||
                page.HasMore != ((long)(page.PageNumber - 1) * page.PageSize + page.Items.Count < page.TotalCount) ||
                page.HasMore && page.Items.Count == 0 ||
                page.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 120 ||
                    string.IsNullOrWhiteSpace(item.Label) || item.Label.Length > 240 || item.Value < 0 ||
                    string.IsNullOrWhiteSpace(item.Unit) || item.Unit.Length > 80) ||
                page.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != page.Items.Count)
                return Result(UiCapabilityResultCode.Unavailable);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Result(UiCapabilityResultCode.Unavailable); }
    }

    public Task<UiCapabilityResult<AnalyticsExportPreview>> ExportPreviewAsync(AnalyticsQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<AnalyticsExportPreview>("analytics-export"));
    }

    private static UiCapabilityResult<Page<AnalyticsMetric>> Result(UiCapabilityResultCode code) =>
        new(code, null, new("analytics", UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, false));
}
