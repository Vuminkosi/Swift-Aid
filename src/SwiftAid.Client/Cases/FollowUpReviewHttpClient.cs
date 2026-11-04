using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

public sealed class FollowUpReviewHttpClient(HttpClient httpClient) : IFollowUpRecommendationReviewClient
{
    public async Task<FollowUpReviewResult> ReviewAsync(Guid caseId, Guid recommendationId, Guid commandId,
        CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null || caseId == Guid.Empty || recommendationId == Guid.Empty || commandId == Guid.Empty)
            return new(FollowUpReviewStatus.Unavailable);
        try
        {
            using var response = await httpClient.PostAsJsonAsync($"api/v1/cases/{caseId:D}/follow-up-recommendations/{recommendationId:D}/review",
                new FollowUpReviewRequest(commandId), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return new(FollowUpReviewStatus.Denied);
            if (response.StatusCode == HttpStatusCode.Conflict) return new(FollowUpReviewStatus.Conflict);
            if (!response.IsSuccessStatusCode) return new(FollowUpReviewStatus.Unavailable);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code) ||
                !Enum.IsDefined(typeof(FollowUpReviewStatus), code)) return new(FollowUpReviewStatus.Unavailable);
            return new((FollowUpReviewStatus)code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        { return new(FollowUpReviewStatus.Unavailable); }
    }
}
