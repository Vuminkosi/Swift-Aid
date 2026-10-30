using System.Net;
using System.Text.Json;
using SwiftAid.Client.Presentation;
using SwiftAid.Mobile.Services.Http;

namespace SwiftAid.Mobile.Services.Presentation;

/// <summary>Case summary through the gateway cookie session.</summary>
public sealed class CaseDetailSummaryHttpClient(CookieSessionHttpClient session) : ICaseDetailSummaryClient
{
    private const string Capability = "case-detail";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CookieSessionHttpClient _session = session ?? throw new ArgumentNullException(nameof(session));

    public async Task<UiCapabilityResult<CaseDetailSummary>> GetAsync(
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
            using var response = await _session.GetAsync(
                $"/api/v1/cases/{caseId:D}/summary",
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
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

            var summary = await ReadAsync<CaseDetailSummary>(response, cancellationToken).ConfigureAwait(false);
            return summary is { IsValid: true }
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

    private static UiCapabilityResult<CaseDetailSummary> Invalid(string message) => new(
        UiCapabilityResultCode.InvalidRequest, default, Metadata(), message);

    private static UiCapabilityResult<CaseDetailSummary> Denied() => new(
        UiCapabilityResultCode.Denied,
        default,
        Metadata(),
        "This case is not available.");

    private static UiCapabilityResult<CaseDetailSummary> Retryable(string message) => new(
        UiCapabilityResultCode.Retryable, default, Metadata(), message);

    private static UiCapabilityResult<CaseDetailSummary> Success(CaseDetailSummary value) => new(
        UiCapabilityResultCode.Success, value, Metadata(), null);

    private static UiCapabilityMetadata Metadata() => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        IsAvailable: true);
}
