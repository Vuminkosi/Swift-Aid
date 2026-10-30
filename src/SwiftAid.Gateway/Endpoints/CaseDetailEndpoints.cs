using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Gateway.Endpoints;

/// <summary>
/// Maps the authenticated staff endpoint for one case summary. Denied,
/// missing, and vanished cases share one outcome that names none of them.
/// </summary>
public static class CaseDetailEndpoints
{
    public static void MapCaseDetailEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var schemes = string.Join(
            ",",
            IdentityConstants.ApplicationScheme,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        endpoints.MapGet("/api/v1/cases/{caseId:guid}/summary", GetSummaryAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = schemes })
            .WithName("GetCaseSummary")
            .Produces<CaseDetailSummary>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetSummaryAsync(
        Guid caseId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (caseId == Guid.Empty)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A case identifier is required.");
        }

        ActorContext? actor;
        try
        {
            actor = TryGetActor(context);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        if (actor is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication is required.");
        }

        // The summary query only exists when the case command pipeline is enabled.
        // A deployment without it fails closed with the same generic 404.
        var summaryQuery = context.RequestServices.GetService<ICaseSummaryQuery>();
        var metadataProvider = context.RequestServices.GetService<IIntakePublicMetadataProvider>();
        if (summaryQuery is null || metadataProvider is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The case was not found.");
        }

        try
        {
            var result = await summaryQuery.GetAsync(actor, caseId, cancellationToken).ConfigureAwait(false);
            if (result.Access != CaseSummaryAccess.Allowed || result.Data is null)
            {
                return result.Access == CaseSummaryAccess.Unavailable
                    ? Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "The case is temporarily unavailable.")
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The case was not found.");
            }

            var labels = await LoadCountryLabelsAsync(metadataProvider, cancellationToken).ConfigureAwait(false);
            var summary = CaseDetailSummaryMapper.ToSummary(result.Data, labels);
            return summary is null
                ? Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The case is temporarily unavailable.")
                : Results.Json(summary, options: JsonOptions, statusCode: StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The case is temporarily unavailable.");
        }
    }

    private static ActorContext? TryGetActor(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return ActorContext.FromValidatedPrincipal(context.User);
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadCountryLabelsAsync(
        IIntakePublicMetadataProvider metadataProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var catalogue = await metadataProvider.GetCountriesAsync(cancellationToken).ConfigureAwait(false);
            return catalogue.Countries.ToDictionary(
                country => country.Code, country => country.Label, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
