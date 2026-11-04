using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Correlation;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

public sealed class CaseCorrelationGatewayClient(AuthenticationStateProvider authentication, IServiceProvider services) : ICaseCorrelationClient
{
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
