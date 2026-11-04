using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Correlation;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

public sealed class CaseCorrelationGatewayClient(AuthenticationStateProvider authentication, IServiceProvider services) : ICaseCorrelationClient
{
    public async Task<CaseCorrelationPage?> GetPageAsync(Guid caseId, string? cursor, CancellationToken cancellationToken = default)
    {
        var principal = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (principal.Identity?.IsAuthenticated != true) return new(false, [], null);
        try
        {
            var actor = ActorContext.FromValidatedPrincipal(principal);
            await using var scope = services.CreateAsyncScope();
            var query = scope.ServiceProvider.GetService<CaseCorrelationQuery>();
            return query is null ? null : await query.GetPageAsync(actor, caseId, cursor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return new(false, [], null); }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }
    public async Task<OrganizationDecisionResult> DecideAsync(Guid caseId, Guid candidateId, OrganizationDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var principal = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (principal.Identity?.IsAuthenticated != true) return new(OrganizationDecisionOutcome.NotFoundOrNotAuthorized);
        try
        {
            var actor = ActorContext.FromValidatedPrincipal(principal);
            await using var scope = services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetService<ICaseCorrelationDecisionService>();
            return service is null ? new(OrganizationDecisionOutcome.Unavailable) : new(await service.DecideAsync(actor, caseId,
                candidateId, request.CommandId, request.Decision, request.Rationale, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return new(OrganizationDecisionOutcome.NotFoundOrNotAuthorized); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(OrganizationDecisionOutcome.Unavailable); }
    }
    public async Task<CaseCorrelationReadResult?> GetAsync(Guid caseId, CancellationToken cancellationToken = default)
    {
        var principal = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (principal.Identity?.IsAuthenticated != true) return new(false, []);
        try
        {
            var actor = ActorContext.FromValidatedPrincipal(principal);
            await using var scope = services.CreateAsyncScope();
            var query = scope.ServiceProvider.GetService<CaseCorrelationQuery>();
            return query is null ? null : await query.GetAsync(actor, caseId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return new(false, []); }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }
}
