using Microsoft.AspNetCore.Mvc;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Owner-scoped entry to proposal planning. Public reporting grants expose no managed case data.</summary>
public static class EllaiGuidanceProposalEndpoint
{
    public static async Task<IResult> NarrateValidationAsync(HttpContext http, string sessionId,
        EllaiValidationNarrationRequest request, [FromServices] IServiceProvider services, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        var result = await NarrateValidationOwnedAsync(ActorContext.FromValidatedPrincipal(http.User), sessionId, request, services, ct);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    internal static async Task<EllaiSessionResponseDto?> NarrateValidationOwnedAsync(ActorContext actor, string sessionId,
        EllaiValidationNarrationRequest request, IServiceProvider services, CancellationToken ct)
    {
        var session = await services.GetRequiredService<IEllaiSessionStore>().GetAsync(sessionId, ct);
        if (session is null || session.OwnerActorId != actor.ActorId) return null;
        var authority = await ResolveAuthorityAsync(services, actor, session.ScopeEpoch, ct);
        return await services.GetRequiredService<EllaiGuidanceProposalAdmission>().PrepareValidationNarrationAsync(
            sessionId, request, authority, services.GetRequiredService<EllaiSessionResponseStore>(), ct);
    }
    public static async Task<IResult> NarrateAsync(HttpContext http, string sessionId,
        EllaiGuidanceNarrationRequest request, [FromServices] IServiceProvider services, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        var result = await NarrateOwnedAsync(ActorContext.FromValidatedPrincipal(http.User), sessionId, request, services, ct);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    internal static async Task<EllaiSessionResponseDto?> NarrateOwnedAsync(ActorContext actor, string sessionId,
        EllaiGuidanceNarrationRequest request, IServiceProvider services, CancellationToken ct)
    {
        var session = await services.GetRequiredService<IEllaiSessionStore>().GetAsync(sessionId, ct);
        if (session is null || session.OwnerActorId != actor.ActorId) return null;
        var authority = await ResolveAuthorityAsync(services, actor, session.ScopeEpoch, ct);
        return await services.GetRequiredService<EllaiGuidanceProposalAdmission>().PrepareNarrationAsync(
            sessionId, request, authority, services.GetRequiredService<EllaiSessionResponseStore>(), ct);
    }

    public static async Task<IResult> ReadAsync(HttpContext http, string sessionId, string proposalId,
        [FromServices] IEllaiSessionStore sessions,
        [FromServices] EllaiGuidanceProposalAdmission admission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(proposalId) || proposalId.Length > 512) return Results.BadRequest();
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var result = await ReadOwnedAsync(actor, sessionId, proposalId, sessions, admission, ct,
            http.RequestServices.GetService<EllaiUiAuthorityResolver>()).ConfigureAwait(false);
        http.Response.Headers.CacheControl = "no-store, private";
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    public static async Task<IResult> PrepareAsync(HttpContext http, string sessionId,
        EllaiGuidancePlanRequest request, [FromServices] IServiceProvider services, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProposalId) || request.ProposalId.Length > 512) return Results.BadRequest();
        http.Response.Headers.CacheControl = "no-store, private";
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var result = await PrepareOwnedAsync(actor, sessionId, request.ProposalId, services, ct).ConfigureAwait(false);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    internal static async Task<EllaiGuidancePlanDto?> PrepareOwnedAsync(ActorContext actor, string sessionId, string proposalId,
        IServiceProvider services, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(proposalId) || proposalId.Length > 512) return null;
        var sessions = services.GetRequiredService<IEllaiSessionStore>();
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != actor.ActorId) return null;
        var authority = await ResolveAuthorityAsync(services, actor, session.ScopeEpoch, ct).ConfigureAwait(false);
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
        if (generated.DiagnosticCode is not null)
            services.GetRequiredService<ILoggerFactory>().CreateLogger("SwiftAid.Ellai.Guidance")
                .LogWarning("Ellai guidance rejected for session {SessionId}: {ReasonCode}",
                    sessionId, generated.DiagnosticCode);
        var refreshedAuthority = await ResolveAuthorityAsync(services, actor, session.ScopeEpoch, ct).ConfigureAwait(false);
        if (!refreshedAuthority.Features.SetEquals(authority.Features) ||
            !refreshedAuthority.Capabilities.SetEquals(authority.Capabilities)) return new("Stale");
        var current = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (current is null || current.OwnerActorId != actor.ActorId || current.ScopeEpoch != session.ScopeEpoch || current.Status != "Active")
            return null;
        var checkpoint = EllaiGuidanceCheckpoint.Deserialize(current.GuidanceJson);
        if (generated.Plan is not null && checkpoint?.Plan?.PlanId != generated.Plan.PlanId)
            return new("Stale");
        return new(generated.Status, generated.Plan, checkpoint?.ContextStamp, checkpoint?.NavigationId,
            checkpoint?.Version ?? 0, checkpoint?.CurrentStepId, checkpoint?.CompletedStepIds, checkpoint?.SkippedStepIds);
    }

    public static async Task<IResult> ControlAsync(HttpContext http, string sessionId,
        EllaiGuidanceControlRequest request, [FromServices] IServiceProvider services, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var result = await ControlOwnedAsync(actor, sessionId, request, services, ct).ConfigureAwait(false);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    internal static async Task<EllaiGuidanceProgressDto?> ControlOwnedAsync(ActorContext actor, string sessionId,
        EllaiGuidanceControlRequest request, IServiceProvider services, CancellationToken ct)
    {
        var session = await services.GetRequiredService<IEllaiSessionStore>().GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != actor.ActorId) return null;
        return await services.GetRequiredService<EllaiGuidanceProposalAdmission>().ControlAsync(
            sessionId, request, await ResolveAuthorityAsync(services, actor, session.ScopeEpoch, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    public static async Task<IResult> CompleteReceiptStepAsync(HttpContext http, string sessionId,
        EllaiGuidanceReceiptRequest request, [FromServices] IServiceProvider services, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var session = await services.GetRequiredService<IEllaiSessionStore>().GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != actor.ActorId) return Results.NotFound();
        if (!http.Request.Headers.TryGetValue("X-SwiftAid-Receipt-Proof", out var values) || values.Count != 1 ||
            !IntakeReceiptProof.TryDecode(values[0], out var proof)) return Results.BadRequest();
        try
        {
            var result = await services.GetRequiredService<EllaiGuidanceProposalAdmission>().CompleteReceiptStepAsync(
                sessionId, request, proof, await ResolveAuthorityAsync(services, actor, session.ScopeEpoch, ct).ConfigureAwait(false),
                services.GetRequiredService<IIntakeSessionEndpointService>(), ct).ConfigureAwait(false);
            return Results.Json(result);
        }
        finally { IntakeReceiptProof.Clear(proof); }
    }

    public static async Task<IResult> CompleteStepAsync(HttpContext http, string sessionId,
        EllaiGuidanceStepRequest request, [FromServices] IServiceProvider services, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        var actor = ActorContext.FromValidatedPrincipal(http.User);
        var result = await CompleteStepOwnedAsync(actor, sessionId, request, services, ct).ConfigureAwait(false);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    internal static async Task<EllaiGuidanceProgressDto?> CompleteStepOwnedAsync(ActorContext actor, string sessionId,
        EllaiGuidanceStepRequest request, IServiceProvider services, CancellationToken ct)
    {
        var sessions = services.GetRequiredService<IEllaiSessionStore>();
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != actor.ActorId) return null;
        return await services.GetRequiredService<EllaiGuidanceProposalAdmission>().CompleteStepAsync(
            sessionId, request, await ResolveAuthorityAsync(services, actor, session.ScopeEpoch, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    private static ValueTask<EllaiUiEntitlements> ResolveAuthorityAsync(IServiceProvider services,
        ActorContext actor, string scopeEpoch, CancellationToken ct) =>
        (services.GetService<EllaiUiAuthorityResolver>() ?? new EllaiUiAuthorityResolver())
            .ResolveAsync(actor, scopeEpoch, ct);

    internal static async Task<EllaiGuidanceAdmissionDto?> ReadOwnedAsync(ActorContext actor, string sessionId, string proposalId,
        IEllaiSessionStore sessions, EllaiGuidanceProposalAdmission admission, CancellationToken ct,
        EllaiUiAuthorityResolver? resolver = null)
    {
        if (string.IsNullOrWhiteSpace(proposalId) || proposalId.Length > 512) return null;
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != actor.ActorId) return null;
        var authority = await (resolver ?? new EllaiUiAuthorityResolver())
            .ResolveAsync(actor, session.ScopeEpoch, ct).ConfigureAwait(false);
        var result = await admission.AdmitAsync(sessionId, proposalId, authority, ct).ConfigureAwait(false);
        return result.Status == "Denied" ? null :
            new EllaiGuidanceAdmissionDto(result.Status, result.Objective, result.InteractionEpoch);
    }
}
