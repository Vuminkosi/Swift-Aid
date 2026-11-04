using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Client.Presentation;

namespace SwiftAid.Mobile.Services.Presentation;

/// <summary>Reads the authorized case correction projection from the Gateway API.</summary>
public sealed class CaseCorrectionReadHttpClient(HttpClient httpClient) : ICaseCorrectionReadClient
{
    private const string Capability = "case-correction-read";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<UiCapabilityResult<CaseDetailCorrectionSnapshot>> GetAsync(
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty)
        {
            return Result(UiCapabilityResultCode.InvalidRequest, null, "Choose a valid case.");
        }

        if (httpClient.BaseAddress is null)
        {
            return Retryable();
        }

        try
        {
            using var response = await httpClient.GetAsync(
                $"api/v1/cases/{caseId:D}/correction", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            {
                return Result(UiCapabilityResultCode.Denied, null, "This case is not available.");
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return Result(UiCapabilityResultCode.InvalidRequest, null, "The correction request is invalid.");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Retryable();
            }

            var snapshot = await response.Content.ReadFromJsonAsync<CaseDetailCorrectionSnapshot>(
                JsonOptions, cancellationToken).ConfigureAwait(false);
            return snapshot is { } value && value.IsValidFor(caseId)
                ? Result(UiCapabilityResultCode.Success, value, null, available: true)
                : Retryable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable();
        }
    }

    private static UiCapabilityResult<CaseDetailCorrectionSnapshot> Retryable() =>
        Result(UiCapabilityResultCode.Retryable, null, "The case is temporarily unavailable. Try again.");

    private static UiCapabilityResult<CaseDetailCorrectionSnapshot> Result(
        UiCapabilityResultCode code,
        CaseDetailCorrectionSnapshot? value,
        string? message,
        bool available = false) => new(
            code,
            value,
            new UiCapabilityMetadata(Capability, UiCapabilityResults.ContractVersion,
                UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, available, message),
            message);
}
