using System.Net;
using System.Net.Http.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

public sealed class CaseCollaboratorCandidatesHttpClient(HttpClient httpClient) : ICaseCollaboratorCandidatesClient
{
    public async Task<CaseCollaboratorCandidatePage?> GetAsync(Guid caseId, string? search, string? after, CancellationToken cancellationToken = default, bool followUpAssignees = false)
    {
        var path = $"api/v1/cases/{caseId:D}/collaborator-candidates?limit=25&search={Uri.EscapeDataString(search ?? "")}&after={Uri.EscapeDataString(after ?? "")}&followUpAssignees={followUpAssignees.ToString().ToLowerInvariant()}";
        using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CaseCollaboratorCandidatePage>(cancellationToken).ConfigureAwait(false);
    }
}
