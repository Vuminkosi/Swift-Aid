using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

public sealed class HealthOfficerAdministrationGatewayClient(AuthenticationStateProvider authentication, IServiceProvider services)
    : IHealthOfficerAdministrationClient
{
    public async Task<HealthOfficerAdministrationLookupResult> LookupAsync(HealthOfficerDesignationLookupRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User);
            await using var scope = services.CreateAsyncScope();
            var query = scope.ServiceProvider.GetService<IHealthOfficerDesignationQuery>();
            if (query is null) return new(HealthOfficerAdministrationStatus.Unavailable);
            var result = await query.GetAsync(actor, new(request.TargetActorId, request.OrganizationId, request.CountryCode, request.Sector), cancellationToken).ConfigureAwait(false);
            var current = result.Current;
            return result.Status switch
            {
                HealthOfficerDesignationReadStatus.Available => new(HealthOfficerAdministrationStatus.Available, current is null ? null :
                    new(current.DesignationId, current.Version, current.Status, current.ValidFromUtc, current.ExpiresAtUtc)),
                HealthOfficerDesignationReadStatus.Denied => new(HealthOfficerAdministrationStatus.Denied),
                HealthOfficerDesignationReadStatus.Invalid => new(HealthOfficerAdministrationStatus.Invalid),
                _ => new(HealthOfficerAdministrationStatus.Unavailable)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return new(HealthOfficerAdministrationStatus.Denied); }
        catch (Exception error) when (error is not OutOfMemoryException) { return new(HealthOfficerAdministrationStatus.Unavailable); }
    }

    public async Task<HealthOfficerAdministrationChangeResult> ChangeAsync(HealthOfficerDesignationChangeRequest request, CancellationToken cancellationToken = default)
    {
        var attempted = false;
        try
        {
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User);
            await using var scope = services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetService<IHealthOfficerDesignationAdministration>();
            if (service is null) return new(HealthOfficerAdministrationStatus.Unavailable);
            attempted = true;
            var result = await service.ChangeAsync(actor, new(request.CommandId, request.TargetActorId, request.OrganizationId,
                request.CountryCode, request.Sector, request.Status, request.ValidFromUtc, request.ExpiresAtUtc, request.ExpectedVersion), cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                HealthOfficerDesignationChangeStatus.Recorded or HealthOfficerDesignationChangeStatus.Replayed
                    when result.DesignationId is { } id && id != Guid.Empty && result.Version is > 0 =>
                    new(result.Status == HealthOfficerDesignationChangeStatus.Recorded ? HealthOfficerAdministrationStatus.Recorded : HealthOfficerAdministrationStatus.Replayed,
                        new(id, result.Version.Value, result.Status == HealthOfficerDesignationChangeStatus.Replayed)),
                HealthOfficerDesignationChangeStatus.Denied => new(HealthOfficerAdministrationStatus.Denied),
                HealthOfficerDesignationChangeStatus.Invalid => new(HealthOfficerAdministrationStatus.Invalid),
                HealthOfficerDesignationChangeStatus.Conflict => new(HealthOfficerAdministrationStatus.Conflict),
                _ => new(HealthOfficerAdministrationStatus.Unavailable)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && attempted)
        { return new(HealthOfficerAdministrationStatus.OutcomeUnknown); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) when (!attempted) { return new(HealthOfficerAdministrationStatus.Denied); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(attempted ? HealthOfficerAdministrationStatus.OutcomeUnknown : HealthOfficerAdministrationStatus.Unavailable); }
    }
}
