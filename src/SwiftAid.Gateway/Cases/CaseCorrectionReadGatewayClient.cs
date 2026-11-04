using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Client.Presentation;

namespace SwiftAid.Gateway.Cases;

/// <summary>Authorized correction projection client for the server-side Gateway.</summary>
public sealed class CaseCorrectionReadGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider) : ICaseCorrectionReadClient
{
    private const string Capability = "case-correction-read";

    public async Task<UiCapabilityResult<CaseDetailCorrectionSnapshot>> GetAsync(
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty)
        {
            return Result(UiCapabilityResultCode.InvalidRequest, null, false, "Choose a valid case.");
        }

        ActorContext? actor;
        try
        {
            var principal = (await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
            actor = principal?.Identity?.IsAuthenticated == true
                ? ActorContext.FromValidatedPrincipal(principal)
                : null;
        }
        catch (UnauthorizedAccessException)
        {
            actor = null;
        }

        if (actor is null)
        {
            return Denied();
        }

        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var query = scope.ServiceProvider.GetService<ICaseCorrectionReadQuery>();
            if (query is null)
            {
                return Unavailable();
            }

            var read = await query.GetAsync(actor, caseId, cancellationToken).ConfigureAwait(false);
            if (read.Access != CaseCorrectionReadAccess.Allowed || read.Data is null)
            {
                return read.Access == CaseCorrectionReadAccess.Unavailable ? Unavailable() : Denied();
            }

            var data = read.Data;
            if (!Enum.IsDefined(data.ReportKind))
            {
                return Unavailable();
            }

            var snapshot = new CaseDetailCorrectionSnapshot(
                data.CaseId,
                data.AggregateVersion,
                data.ReportRevision,
                (CaseDetailCorrectionReportKind)(int)data.ReportKind,
                data.CanCorrectReport,
                data.Narrative);
            return snapshot.IsValidFor(caseId)
                ? Result(UiCapabilityResultCode.Success, snapshot, true, null)
                : Unavailable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable();
        }
    }

    private static UiCapabilityResult<CaseDetailCorrectionSnapshot> Denied() =>
        Result(UiCapabilityResultCode.Denied, null, false, "This case is not available.");

    private static UiCapabilityResult<CaseDetailCorrectionSnapshot> Unavailable() =>
        Result(UiCapabilityResultCode.Unavailable, null, false, "The case is temporarily unavailable.");

    private static UiCapabilityResult<CaseDetailCorrectionSnapshot> Result(
        UiCapabilityResultCode code,
        CaseDetailCorrectionSnapshot? value,
        bool available,
        string? message) => new(
            code,
            value,
            new UiCapabilityMetadata(Capability, UiCapabilityResults.ContractVersion,
                UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, available, message),
            message);
}
