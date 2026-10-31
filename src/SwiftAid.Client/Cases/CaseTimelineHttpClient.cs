using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

/// <summary>Staff case-timeline client over the authenticated Gateway API.</summary>
/// <remarks>
/// Renders immutable event metadata only; sealed report content is never in these
/// payloads, so this client cannot display or persist it.
/// </remarks>
public sealed class CaseTimelineHttpClient(HttpClient httpClient) : ICaseTimelineClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CaseTimelineResult> GetAsync(
        Guid caseId,
        int? limit,
        CancellationToken cancellationToken = default,
        long? beforeAggregateVersion = null)
    {
        if (httpClient.BaseAddress is null || caseId == Guid.Empty)
        {
            return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        }

        var path = $"api/v1/cases/{caseId:D}/timeline";
        var queryParameters = new List<string>(2);
        if (limit.HasValue)
        {
            queryParameters.Add($"limit={limit.Value}");
        }
        if (beforeAggregateVersion.HasValue)
        {
            queryParameters.Add($"beforeAggregateVersion={beforeAggregateVersion.Value}");
        }
        if (queryParameters.Count > 0) path += "?" + string.Join("&", queryParameters);

        using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        }

        try
        {
            var timeline = await response.Content
                .ReadFromJsonAsync<CaseTimeline>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return timeline is null
                ? new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null)
                : new CaseTimelineResult(CaseTimelineAccess.Found, timeline);
        }
        catch (JsonException)
        {
            return new CaseTimelineResult(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        }
    }
}
