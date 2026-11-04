using System.Net;
using System.Text.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Mobile.Services.Presentation;

/// <summary>Authorized report evidence through the authenticated gateway transport.</summary>
public sealed class CaseReportDetailsHttpClient(HttpClient client) : ICaseReportDetailsClient
{
    private const string Capability = "case-detail";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<UiCapabilityResult<CaseReportDetails>> GetAsync(
        Guid caseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caseId == Guid.Empty)
        {
            return Invalid("Choose a valid case.");
        }

        try
        {
            using var response = await _client.GetAsync(
                $"/api/v1/cases/{caseId:D}/report-details",
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Denied();
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Denied();
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return Invalid("Choose a valid case.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Retryable("The case is temporarily unavailable.");
            }

            var summary = await ReadAsync<CaseReportDetails>(response, cancellationToken).ConfigureAwait(false);
            return summary is not null && summary.CaseId == caseId
                ? Success(summary)
                : Retryable("The case is temporarily unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Retryable("The case is temporarily unavailable.");
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static UiCapabilityResult<CaseReportDetails> Invalid(string message) => new(
        UiCapabilityResultCode.InvalidRequest, default, Metadata(), message);

    private static UiCapabilityResult<CaseReportDetails> Denied() => new(
        UiCapabilityResultCode.Denied,
        default,
        Metadata(),
        "This case is not available.");

    private static UiCapabilityResult<CaseReportDetails> Retryable(string message) => new(
        UiCapabilityResultCode.Retryable, default, Metadata(), message);

    private static UiCapabilityResult<CaseReportDetails> Success(CaseReportDetails value) => new(
        UiCapabilityResultCode.Success, value, Metadata(), null);

    private static UiCapabilityMetadata Metadata() => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: true);
}
