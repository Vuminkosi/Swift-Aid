using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

public sealed class EligibleHealthOfficerGatewayClient(AuthenticationStateProvider authentication, IServiceProvider services)
    : IEligibleHealthOfficerClient
{
    public async Task<EligibleHealthOfficerLookupResult> GetAsync(Guid caseId, CancellationToken cancellationToken = default)
    {
        var principal = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (principal.Identity?.IsAuthenticated != true) return new(EligibleHealthOfficerLookupStatus.Denied);
        if (caseId == Guid.Empty) return new(EligibleHealthOfficerLookupStatus.Unavailable);
        try
        {
            var actor = ActorContext.FromValidatedPrincipal(principal);
            var query = services.GetService<IEligibleHealthOfficerQuery>();
            if (query is null) return new(EligibleHealthOfficerLookupStatus.Unavailable);
            var page = await query.GetAsync(actor, caseId, 20, cancellationToken).ConfigureAwait(false);
            return page is null ? new(EligibleHealthOfficerLookupStatus.Denied) :
                new(EligibleHealthOfficerLookupStatus.Available, new(page.Items.Select(item =>
                    new EligibleHealthOfficerCandidate(item.DesignationId, item.DesignationVersion, item.ActorId, item.DisplayLabel)).ToArray(), page.HasMore));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return new(EligibleHealthOfficerLookupStatus.Denied); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(EligibleHealthOfficerLookupStatus.Unavailable); }
    }
}
