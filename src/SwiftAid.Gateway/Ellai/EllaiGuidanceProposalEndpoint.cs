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

    public static async Task<IResult> PrepareAsync(HttpContext http, string sessionId,
        EllaiGuidancePlanRequest request, [FromServices] IServiceProvider services, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProposalId) || request.ProposalId.Length > 512) return Results.BadRequest();
        http.Response.Headers.CacheControl = "no-store, private";
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var result = await PrepareOwnedAsync(actor.ActorId, sessionId, request.ProposalId, services, ct).ConfigureAwait(false);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    internal static async Task<EllaiGuidancePlanDto?> PrepareOwnedAsync(string actorId, string sessionId, string proposalId,
        IServiceProvider services, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(proposalId) || proposalId.Length > 512) return null;
        var sessions = services.GetRequiredService<IEllaiSessionStore>();
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != actorId) return null;
        var authority = PublicReportAuthority(actorId, session.ScopeEpoch);
        var admission = services.GetRequiredService<EllaiGuidanceProposalAdmission>();
        // Resolve provider dependencies only after ownership and proposal admission succeed.
        var admitted = await admission.AdmitAsync(sessionId, proposalId, authority, ct).ConfigureAwait(false);
        if (admitted.Status == "Denied") return null;
        if (admitted.Status != "Ready")
            return new(admitted.Status, NavigationId: admitted.Catalogue?.Descriptors.FirstOrDefault()?.NavigationId);
        var generated = await admission.PlanAsync(sessionId, proposalId, authority,
            services.GetRequiredService<EllaiModelGuidancePlanner>(),
            services.GetRequiredService<IEllaiUiDiscoveryService>(),
            services.GetRequiredService<EllaiGuidancePlanValidator>(),
            services.GetRequiredService<EllaiUsageLedger>(),
            services.GetRequiredService<IEllaiUsageStore>(), ct).ConfigureAwait(false);
        var current = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (current is null || current.OwnerActorId != actorId || current.ScopeEpoch != session.ScopeEpoch || current.Status != "Active")
            return null;
        var checkpoint = EllaiGuidanceCheckpoint.Deserialize(current.GuidanceJson);
        if (generated.Plan is not null && checkpoint?.Plan?.PlanId != generated.Plan.PlanId)
            return new("Stale");
        return new(generated.Status, generated.Plan, checkpoint?.ContextStamp, checkpoint?.NavigationId);
    }

    private static EllaiUiEntitlements PublicReportAuthority(string actorId, string scopeEpoch) =>
        new(actorId, scopeEpoch, new HashSet<string>(StringComparer.Ordinal) { EllaiReportUiManifest.FeatureId },
            new HashSet<string>(StringComparer.Ordinal));

    internal static async Task<EllaiGuidanceAdmissionDto?> ReadOwnedAsync(string actorId, string sessionId, string proposalId,
        IEllaiSessionStore sessions, EllaiGuidanceProposalAdmission admission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(proposalId) || proposalId.Length > 512) return null;
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != actorId) return null;
        // This first route exposes only the public report capability, which requires no staff grant.
        // Privileged capabilities must be added through a fresh scope service, never client metadata.
        var authority = PublicReportAuthority(actorId, session.ScopeEpoch);
        var result = await admission.AdmitAsync(sessionId, proposalId, authority, ct).ConfigureAwait(false);
        return result.Status == "Denied" ? null :
            new EllaiGuidanceAdmissionDto(result.Status, result.Objective, result.InteractionEpoch);
    }
}
