using System.Net;
using System.Text.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

public sealed class EligibleHealthOfficerHttpClient(HttpClient httpClient) : IEligibleHealthOfficerClient
{
    public async Task<EligibleHealthOfficerLookupResult> GetAsync(Guid caseId, CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null || caseId == Guid.Empty) return new(EligibleHealthOfficerLookupStatus.Unavailable);
        try
        {
            using var response = await httpClient.GetAsync($"api/v1/cases/{caseId:D}/eligible-health-officers", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return new(EligibleHealthOfficerLookupStatus.Denied);
            if (!response.IsSuccessStatusCode) return new(EligibleHealthOfficerLookupStatus.Unavailable);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                !document.RootElement.TryGetProperty("hasMore", out var hasMore) ||
                hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new(EligibleHealthOfficerLookupStatus.Unavailable);
            var page = document.RootElement.Deserialize<EligibleHealthOfficerCandidatePage>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (page?.Items is null || page.Items.Count > 20 || page.Items.Any(item => item is null ||
                item.DesignationId == Guid.Empty || item.DesignationVersion < 1 || string.IsNullOrWhiteSpace(item.ActorId) ||
                item.ActorId.Length > 256 || item.ActorId.Any(char.IsControl) ||
                (item.DisplayLabel is { } label && (label.Length > 120 || label.Any(char.IsControl)))))
                return new(EligibleHealthOfficerLookupStatus.Unavailable);
            return new(EligibleHealthOfficerLookupStatus.Available, page);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException)
        { return new(EligibleHealthOfficerLookupStatus.Unavailable); }
    }
}
