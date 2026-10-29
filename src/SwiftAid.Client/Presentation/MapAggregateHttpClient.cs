using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Client.Presentation;

/// <summary>Staff map aggregate client over the authenticated Gateway API.</summary>
public sealed class MapAggregateHttpClient(HttpClient httpClient) : IMapAggregateClient
{
    private const string Capability = "map-aggregates";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<UiCapabilityResult<MapAggregatePage>> GetAsync(
        MapAggregateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (httpClient.BaseAddress is null || !query.IsValid)
        {
            return Unavailable("Map totals are not available right now.");
        }

        var address = $"api/v1/surveillance/area-aggregates?period={(query.Period == MapAggregatePeriod.Days90 ? "days90" : "days30")}";
        if (!string.IsNullOrWhiteSpace(query.RegionCode))
        {
            address += $"&regionCode={Uri.EscapeDataString(query.RegionCode)}";
        }

        try
        {
            using var response = await httpClient.GetAsync(address, cancellationToken).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => await ReadResultAsync(query, response, cancellationToken).ConfigureAwait(false),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound => Denied(),
                HttpStatusCode.BadRequest => Invalid("Review the map filters and try again."),
                _ => Unavailable("Map totals are not available right now.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Map totals are not available right now.");
        }
    }

    private static async Task<UiCapabilityResult<MapAggregatePage>> ReadResultAsync(
        MapAggregateQuery query,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        SurveillanceAggregateResult? result;
        try
        {
            result = await response.Content
                .ReadFromJsonAsync<SurveillanceAggregateResult>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Unavailable("Map totals could not be checked. Refresh to try again.");
        }

        if (result is not { Access: SurveillanceAggregateAccess.Allowed })
        {
            return Unavailable("Map totals are not available right now.");
        }

        var page = ToPage(query, result);
        return page.IsValid
            ? Success(page, result.Truncated ? "Showing totals for the most recent readable cases." : null)
            : Unavailable("Map totals could not be checked. Refresh to try again.");
    }

    private static MapAggregatePage ToPage(MapAggregateQuery query, SurveillanceAggregateResult result)
    {
        var areas = result.Areas
            .Skip((query.Page.PageNumber - 1) * query.Page.PageSize)
            .Take(query.Page.PageSize)
            .Select(ToArea)
            .ToArray();
        return new MapAggregatePage(
            new Page<MapAreaAggregate>(
                areas,
                query.Page.PageNumber,
                query.Page.PageSize,
                result.Areas.Count,
                query.Page.PageNumber * query.Page.PageSize < result.Areas.Count),
            result.AsOfUtc);
    }

    private static MapAreaAggregate ToArea(SurveillanceAggregateArea area) => new(
        area.AreaCode,
        area.AreaLabel,
        area.Precision switch
        {
            SurveillanceAggregatePrecision.Country => MapAggregateAreaPrecision.Country,
            SurveillanceAggregatePrecision.Region => MapAggregateAreaPrecision.Region,
            _ => MapAggregateAreaPrecision.Region
        },
        area.HumanCount,
        area.AnimalCount,
        area.Denominator,
        area.IsSuppressed,
        area.SuppressionReason,
        area.AsOfUtc);

    private static UiCapabilityResult<MapAggregatePage> Success(MapAggregatePage page, string? message) => new(
        UiCapabilityResultCode.Success,
        page,
        Metadata(available: true),
        message);

    private static UiCapabilityResult<MapAggregatePage> Invalid(string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        Metadata(available: false),
        message);

    private static UiCapabilityResult<MapAggregatePage> Denied() => new(
        UiCapabilityResultCode.Denied,
        default,
        Metadata(available: false),
        "You do not have access to these map totals.");

    private static UiCapabilityResult<MapAggregatePage> Unavailable(string message) => new(
        UiCapabilityResultCode.Unavailable,
        default,
        Metadata(available: false, message),
        message);

    private static UiCapabilityMetadata Metadata(bool available, string? notice = null) => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        available,
        notice);
}
