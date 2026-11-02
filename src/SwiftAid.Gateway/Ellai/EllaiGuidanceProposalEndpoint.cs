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
        var result = await ReadOwnedAsync(actor.ActorId, sessionId, proposalId, sessions, admission, ct).ConfigureAwait(false);
        http.Response.Headers.CacheControl = "no-store, private";
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    internal static async Task<EllaiGuidanceAdmissionDto?> ReadOwnedAsync(string actorId, string sessionId, string proposalId,
        IEllaiSessionStore sessions, EllaiGuidanceProposalAdmission admission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(proposalId) || proposalId.Length > 512) return null;
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != actorId) return null;
        // This first route exposes only the public report capability, which requires no staff grant.
        // Privileged capabilities must be added through a fresh scope service, never client metadata.
        var authority = new EllaiUiEntitlements(actorId, session.ScopeEpoch,
            new HashSet<string>(StringComparer.Ordinal) { EllaiReportUiManifest.FeatureId },
            new HashSet<string>(StringComparer.Ordinal));
        var result = await admission.AdmitAsync(sessionId, proposalId, authority, ct).ConfigureAwait(false);
        return result.Status == "Denied" ? null :
            new EllaiGuidanceAdmissionDto(result.Status, result.Objective, result.InteractionEpoch);
    }
}
