using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Gateway.Cases;

public sealed class CaseReportDetailsGatewayClient(
    AuthenticationStateProvider authentication,
    IServiceScopeFactory scopes,
    ILogger<CaseReportDetailsGatewayClient> logger) : ICaseReportDetailsClient
{
    public async Task<UiCapabilityResult<CaseReportDetails>> GetAsync(Guid caseId, CancellationToken ct = default)
    {
        try
        {
            var user = (await authentication.GetAuthenticationStateAsync()).User;
            if (user.Identity?.IsAuthenticated != true) return Result(UiCapabilityResultCode.Denied);
            var actor = ActorContext.FromValidatedPrincipal(user);
            await using var scope = scopes.CreateAsyncScope();
            var query = scope.ServiceProvider.GetService<ICaseReportReadQuery>();
            if (query is null) return Result(UiCapabilityResultCode.Unavailable);
            var report = await query.GetAsync(actor, caseId, ct);
            return report is null ? Result(UiCapabilityResultCode.Denied) : Result(UiCapabilityResultCode.Success, report);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return Result(UiCapabilityResultCode.Denied); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Authorized case report read failed.");
            return Result(UiCapabilityResultCode.Unavailable);
        }
    }

    private static UiCapabilityResult<CaseReportDetails> Result(UiCapabilityResultCode code, CaseReportDetails? value = null) =>
        new(code, value, new("case-report", UiCapabilityResults.ContractVersion,
            UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, code == UiCapabilityResultCode.Success));
}
