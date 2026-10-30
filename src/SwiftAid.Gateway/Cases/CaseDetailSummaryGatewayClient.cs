using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Intake;
using SwiftAid.Client.Presentation;

namespace SwiftAid.Gateway.Cases;

/// <summary>
/// Server-side case summary client for Blazor Server: resolves the current
/// staff principal per call and delegates to the authorized summary query.
/// Denied, missing, and vanished cases share one outcome that names none of
/// them. Deployments without the case command pipeline fail closed the same way.
/// </summary>
public sealed class CaseDetailSummaryGatewayClient(
    AuthenticationStateProvider authenticationStateProvider,
    IServiceProvider serviceProvider,
    IIntakePublicMetadataProvider metadataProvider) : ICaseDetailSummaryClient
{
    private const string Capability = "case-detail";

    private readonly AuthenticationStateProvider _authenticationStateProvider =
        authenticationStateProvider ?? throw new ArgumentNullException(nameof(authenticationStateProvider));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly IIntakePublicMetadataProvider _metadataProvider =
        metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));

    public async Task<UiCapabilityResult<CaseDetailSummary>> GetAsync(
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty)
        {
            return Invalid("Choose a valid case.");
        }

        var actor = await GetActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return Denied();
        }

        var summaryQuery = _serviceProvider.GetService<ICaseSummaryQuery>();
        if (summaryQuery is null)
        {
            return Unavailable("The case is temporarily unavailable.");
        }

        try
        {
            var result = await summaryQuery.GetAsync(actor, caseId, cancellationToken).ConfigureAwait(false);
            if (result.Access != CaseSummaryAccess.Allowed || result.Data is null)
            {
                return result.Access == CaseSummaryAccess.Unavailable
                    ? Unavailable("The case is temporarily unavailable.")
                    : Denied();
            }

            var labels = await LoadCountryLabelsAsync(cancellationToken).ConfigureAwait(false);
            var summary = CaseDetailSummaryMapper.ToSummary(result.Data, labels);
            return summary is null
                ? Unavailable("The case is temporarily unavailable.")
                : Success(summary);
        }
        catch (Exception)
        {
            return Unavailable("The case is temporarily unavailable.");
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadCountryLabelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var catalogue = await _metadataProvider.GetCountriesAsync(cancellationToken).ConfigureAwait(false);
            return catalogue.Countries.ToDictionary(
                country => country.Code, country => country.Label, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static UiCapabilityResult<CaseDetailSummary> Success(CaseDetailSummary summary) => new(
        UiCapabilityResultCode.Success,
        summary,
        Metadata(available: true),
        null);

    private static UiCapabilityResult<CaseDetailSummary> Invalid(string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        Metadata(available: false),
        message);

    private static UiCapabilityResult<CaseDetailSummary> Denied() => new(
        UiCapabilityResultCode.Denied,
        default,
        Metadata(available: false),
        "This case is not available.");

    private static UiCapabilityResult<CaseDetailSummary> Unavailable(string message) => new(
        UiCapabilityResultCode.Unavailable,
        default,
        Metadata(available: false, message),
        message);

    private static UiCapabilityMetadata Metadata(bool available, string? notice = null) => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        available,
        notice);

    private async Task<ActorContext?> GetActorAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var principal = state.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        try
        {
            return ActorContext.FromValidatedPrincipal(principal);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
