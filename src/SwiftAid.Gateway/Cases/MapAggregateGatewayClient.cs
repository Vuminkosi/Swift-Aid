using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Gateway.Cases;

/// <summary>
/// Server-side map aggregate client for Blazor Server: resolves the current
/// staff principal per call and delegates to the authorized aggregate query.
/// All data rules (ownership scope, per-case ReadCase enforcement, skipped
/// denials, small-count suppression) are enforced by the query, not by this
/// adapter. Deployments without the case command pipeline fail closed with
/// the same unavailable outcome.
/// </summary>
public sealed class MapAggregateGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider) : IMapAggregateClient
{
    private const string Capability = "map-aggregates";

    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public async Task<UiCapabilityResult<MapAggregatePage>> GetAsync(
        MapAggregateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsValid)
        {
            return Invalid("Review the map filters and try again.");
        }

        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return Denied();
        }

        await using var scope = _serviceProvider.CreateAsyncScope();
        var aggregateQuery = scope.ServiceProvider.GetService<IMapAggregateQuery>();
        if (aggregateQuery is null)
        {
            return Unavailable("Map totals are not available right now.");
        }

        try
        {
            var result = await aggregateQuery.GetAsync(
                actor,
                query.RegionCode,
                TimeSpan.FromDays((int)query.Period),
                cancellationToken).ConfigureAwait(false);
            if (result.Access != SurveillanceAggregateAccess.Allowed)
            {
                return Unavailable("Map totals are not available right now.");
            }

            var page = ToPage(query, result);
            return page.IsValid
                ? Success(page, result.Truncated ? "Showing totals for the most recent readable cases." : null)
                : Unavailable("Map totals could not be checked. Refresh to try again.");
        }
        catch (Exception)
        {
            return Unavailable("Map totals are not available right now.");
        }
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

    private async Task<ActorContext?> GetActorAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = state.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        try
        {
            return ActorContext.FromValidatedPrincipal(principal);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
