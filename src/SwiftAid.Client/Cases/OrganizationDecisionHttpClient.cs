using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

public sealed class OrganizationDecisionHttpClient(HttpClient httpClient) : IOrganizationSuggestionDecisionClient
{
    public async Task<OrganizationDecisionResult> DecideAsync(Guid caseId, Guid recommendationId, OrganizationDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null || caseId == Guid.Empty || recommendationId == Guid.Empty || request is null || request.CommandId == Guid.Empty)
            return new(OrganizationDecisionOutcome.Unavailable);
        try
        {
            using var response = await httpClient.PostAsJsonAsync($"api/v1/cases/{caseId:D}/organization-suggestions/{recommendationId:D}/decision",
                request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return new(OrganizationDecisionOutcome.NotFoundOrNotAuthorized);
            if (response.StatusCode == HttpStatusCode.Conflict) return new(OrganizationDecisionOutcome.Conflict);
            if (!response.IsSuccessStatusCode) return new(OrganizationDecisionOutcome.Unavailable);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code) ||
                !Enum.IsDefined(typeof(OrganizationDecisionOutcome), code)) return new(OrganizationDecisionOutcome.Unavailable);
            return new((OrganizationDecisionOutcome)code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        { return new(OrganizationDecisionOutcome.Unavailable); }
    }
}

