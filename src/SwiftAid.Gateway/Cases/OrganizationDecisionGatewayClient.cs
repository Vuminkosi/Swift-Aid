using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

public sealed class OrganizationDecisionGatewayClient(AuthenticationStateProvider authentication, IServiceProvider services)
    : IOrganizationSuggestionDecisionClient
{
    public async Task<OrganizationDecisionResult> DecideAsync(Guid caseId, Guid suggestionId, OrganizationDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var principal = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (principal.Identity?.IsAuthenticated != true) return new(OrganizationDecisionOutcome.NotFoundOrNotAuthorized);
        try
        {
            var actor = ActorContext.FromValidatedPrincipal(principal);
            await using var scope = services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetService<IOrganizationSuggestionDecisionService>();
            if (service is null) return new(OrganizationDecisionOutcome.Unavailable);
            return new(await service.DecideAsync(actor, caseId, suggestionId, request.CommandId, request.Decision, request.Rationale, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return new(OrganizationDecisionOutcome.NotFoundOrNotAuthorized); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(OrganizationDecisionOutcome.Unavailable); }
    }
}
