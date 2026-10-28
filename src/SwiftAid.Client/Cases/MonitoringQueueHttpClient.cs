using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

/// <summary>Staff monitoring queue client over the authenticated Gateway API.</summary>
/// <remarks>
/// Denial and absence share one outcome so callers cannot probe for case
/// existence; the endpoint already returns an empty allowed list for denied
/// per-case rows.
/// </remarks>
public sealed class MonitoringQueueHttpClient(HttpClient httpClient) : IMonitoringQueueClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<MonitoringQueueResult> GetAssignedAsync(
        int? limit, CancellationToken cancellationToken = default) =>
        GetAsync(MonitoringQueueScope.Assigned, limit, cancellationToken);

    public Task<MonitoringQueueResult> GetOpenAsync(
        int? limit, CancellationToken cancellationToken = default) =>
        GetAsync(MonitoringQueueScope.Open, limit, cancellationToken);

    private async Task<MonitoringQueueResult> GetAsync(
        MonitoringQueueScope scope, int? limit, CancellationToken cancellationToken)
    {
        if (httpClient.BaseAddress is null)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Unavailable, []);
        }

        var address = $"api/v1/cases/monitoring/queue?scope={scope}";
        if (limit.HasValue)
        {
            address += $"&limit={limit.Value}";
        }

        try
        {
            using var response = await httpClient.GetAsync(address, cancellationToken).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => await MapResultAsync(response, cancellationToken).ConfigureAwait(false),
                HttpStatusCode.NotFound =>
                    new MonitoringQueueResult(CaseQueueAccess.Denied, []),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new MonitoringQueueResult(CaseQueueAccess.Denied, []),
                HttpStatusCode.BadRequest =>
                    new MonitoringQueueResult(CaseQueueAccess.Unavailable, []),
                _ => new MonitoringQueueResult(CaseQueueAccess.Unavailable, []),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Unavailable, []);
        }
    }

    private static async Task<MonitoringQueueResult> MapResultAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var result = await response.Content
                .ReadFromJsonAsync<MonitoringQueueResult>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (result is null)
            {
                return new MonitoringQueueResult(CaseQueueAccess.Unavailable, []);
            }

            return result;
        }
        catch (JsonException)
        {
            return new MonitoringQueueResult(CaseQueueAccess.Unavailable, []);
        }
    }
}
