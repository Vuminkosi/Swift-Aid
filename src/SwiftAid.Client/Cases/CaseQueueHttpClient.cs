using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

/// <summary>Staff case queue client over the authenticated Gateway API.</summary>
public sealed class CaseQueueHttpClient(HttpClient httpClient) : ICaseQueueClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default)
        => await GetMineAsync(limit, null, cancellationToken).ConfigureAwait(false);

    public async Task<CaseQueueResult> GetMineAsync(
        int? limit, string? cursor, CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null || limit is <= 0 or > 500 || cursor?.Length > 49)
        {
            return new CaseQueueResult(CaseQueueAccess.Unavailable, []);
        }

        var query = new List<string>();
        if (limit.HasValue) query.Add($"limit={limit.Value}");
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        var address = "api/v1/cases/mine" + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query));

        try
        {
            using var response = await httpClient.GetAsync(address, cancellationToken).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => await ReadResultAsync(response, cancellationToken).ConfigureAwait(false),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                    new CaseQueueResult(CaseQueueAccess.Denied, []),
                _ => new CaseQueueResult(CaseQueueAccess.Unavailable, [])
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new CaseQueueResult(CaseQueueAccess.Unavailable, []);
        }
    }

    private static async Task<CaseQueueResult> ReadResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<CaseQueueResult>(JsonOptions, cancellationToken)
                       .ConfigureAwait(false)
                   ?? new CaseQueueResult(CaseQueueAccess.Unavailable, []);
        }
        catch (JsonException)
        {
            return new CaseQueueResult(CaseQueueAccess.Unavailable, []);
        }
    }
}
