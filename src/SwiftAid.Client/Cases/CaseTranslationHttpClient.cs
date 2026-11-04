using System.Net;
using System.Net.Http.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

public sealed class CaseTranslationHttpClient(HttpClient http) : ICaseTranslationClient
{
    public async Task<CaseTranslationSnapshot> GetAsync(Guid caseId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync($"api/v1/cases/{caseId:D}/translations", cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return new(caseId, [], CaseClassificationAccess.Denied);
            if (response.IsSuccessStatusCode)
            {
                var snapshot = await response.Content.ReadFromJsonAsync<CaseTranslationSnapshot>(cancellationToken);
                if (snapshot?.IsValidFor(caseId) == true) return snapshot;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { }
        return new(caseId, [], CaseClassificationAccess.Unavailable);
    }

    public async Task<string> ReviewAsync(Guid caseId, Guid artifactId, Guid decisionId, long expectedVersion, bool approve, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"api/v1/cases/{caseId:D}/translations/{artifactId:D}/reviews",
                new { decisionId, expectedVersion, approve }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict) return "Conflict";
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ReviewResponse>(cancellationToken);
                if (result?.State is "Recorded" or "Replayed") return result.State;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { }
        return "Unavailable";
    }
    private sealed record ReviewResponse(string State);
}
