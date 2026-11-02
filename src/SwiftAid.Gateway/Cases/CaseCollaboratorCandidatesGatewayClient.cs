using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

public sealed class CaseCollaboratorCandidatesGatewayClient(AuthenticationStateProvider authentication, IServiceProvider services) : ICaseCollaboratorCandidatesClient
{
    public async Task<CaseCollaboratorCandidatePage?> GetAsync(Guid caseId, string? search, string? after, CancellationToken cancellationToken = default)
    {
        var principal = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (principal.Identity?.IsAuthenticated != true) return null;
        ActorContext actor;
        try { actor = ActorContext.FromValidatedPrincipal(principal); }
        catch (UnauthorizedAccessException) { return null; }
        await using var scope = services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetService<ICaseCollaboratorCandidatesQuery>();
        return query is null ? null : await query.GetAsync(actor, caseId, search, after, 25, cancellationToken).ConfigureAwait(false);
    }
}
