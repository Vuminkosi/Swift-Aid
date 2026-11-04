using System.Net;
using System.Net.Http.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

public sealed class CaseClassificationHttpClient(HttpClient http) : ICaseClassificationClient
{
    public async Task<CaseClassificationSnapshot> GetAsync(Guid caseId, CancellationToken cancellationToken = default)
    {
        if (caseId == Guid.Empty) return new(caseId, [], CaseClassificationAccess.Unavailable);
        try
        {
            using var response = await http.GetAsync($"api/v1/cases/{caseId:D}/classification", cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return new(caseId, [], CaseClassificationAccess.Denied);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var snapshot = await response.Content.ReadFromJsonAsync<CaseClassificationSnapshot>(cancellationToken);
                if (snapshot is not null && snapshot.IsValidFor(caseId)) return snapshot;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* Never render transport diagnostics or preserve stale evidence. */ }
        return new(caseId, [], CaseClassificationAccess.Unavailable);
    }
}
