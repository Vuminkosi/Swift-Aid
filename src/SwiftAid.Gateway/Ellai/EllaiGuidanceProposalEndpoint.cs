using Microsoft.AspNetCore.Mvc;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Owner-scoped entry to proposal planning. Public reporting grants expose no managed case data.</summary>
public static class EllaiGuidanceProposalEndpoint
{
    public static async Task<IResult> ReadAsync(HttpContext http, string sessionId, string proposalId,
        [FromServices] IEllaiSessionStore sessions,
        [FromServices] EllaiGuidanceProposalAdmission admission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(proposalId) || proposalId.Length > 512) return Results.BadRequest();
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != actor.ActorId) return Results.NotFound();
        // This first route exposes only the public report capability, which requires no staff grant.
        // Privileged capabilities must be added through a fresh scope service, never client metadata.
        var authority = new EllaiUiEntitlements(actor.ActorId, session.ScopeEpoch,
            new HashSet<string>(StringComparer.Ordinal) { EllaiReportUiManifest.FeatureId },
            new HashSet<string>(StringComparer.Ordinal));
        var result = await admission.AdmitAsync(sessionId, proposalId, authority, ct).ConfigureAwait(false);
        http.Response.Headers.CacheControl = "no-store, private";
        return result.Status == "Denied" ? Results.NotFound() :
            Results.Json(new EllaiGuidanceAdmissionDto(result.Status, result.Objective, result.InteractionEpoch));
    }
}
