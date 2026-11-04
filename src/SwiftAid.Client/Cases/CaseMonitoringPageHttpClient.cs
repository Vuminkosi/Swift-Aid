using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

public sealed class CaseMonitoringPageHttpClient(HttpClient httpClient) : ICaseMonitoringPageClient
{
    public async Task<CaseMonitoringPageResult> GetAsync(Guid caseId, int offset, int limit, long version,
        CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null || caseId == Guid.Empty || offset < 0 || limit is < 1 or > 100 || version < 1)
            return new(CaseMonitoringPageAccess.Unavailable, null);
        using var response = await httpClient.GetAsync(
            $"api/v1/cases/{caseId:D}/monitoring?offset={offset}&limit={limit}&version={version}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new(CaseMonitoringPageAccess.NotFoundOrNotAuthorized, null);
        if (response.StatusCode == HttpStatusCode.Conflict) return new(CaseMonitoringPageAccess.Stale, null);
        if (!response.IsSuccessStatusCode) return new(CaseMonitoringPageAccess.Unavailable, null);
        try
        {
            var page = await response.Content.ReadFromJsonAsync<CaseMonitoringPage>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page is null || page.CaseId != caseId || page.AggregateVersion != version || page.Items is null ||
                page.Items.Count > limit || page.Items.Any(item => item?.Work is null || item.Work.CaseId != caseId ||
                    item.Work.WorkItemId == Guid.Empty || !Enum.IsDefined(item.Work.Kind) || item.Work.AggregateVersion <= 0 ||
                    item.Work.AggregateVersion > version || string.IsNullOrWhiteSpace(item.Work.Status)) ||
                page.NextOffset is { } next && (next != offset + limit || page.Items.Count != limit))
                return new(CaseMonitoringPageAccess.Unavailable, null);
            return new(CaseMonitoringPageAccess.Found, page);
        }
        catch (JsonException) { return new(CaseMonitoringPageAccess.Unavailable, null); }
    }
}
