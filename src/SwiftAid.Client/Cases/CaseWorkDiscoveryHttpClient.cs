using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Cases;

public sealed class CaseWorkDiscoveryHttpClient(HttpClient httpClient) : ICaseWorkDiscoveryClient
{
    public async Task<CaseWorkDiscoveryPage> GetAsync(CaseWorkDiscoveryKind kind, CaseWorkDiscoveryScope scope,
        int limit, Guid? cursor, CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null || !Enum.IsDefined(kind) || !Enum.IsDefined(scope) || limit is < 1 or > 100 ||
            cursor == Guid.Empty || kind == CaseWorkDiscoveryKind.Escalation && scope == CaseWorkDiscoveryScope.Assigned)
            return new(CaseQueueAccess.Unavailable, []);
        var path = $"api/v1/cases/monitoring/cases?kind={kind}&scope={scope}&limit={limit}";
        if (cursor is { } position) path += $"&cursor={position:N}";
        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(CaseQueueAccess.Denied, []);
            if (!response.IsSuccessStatusCode) return new(CaseQueueAccess.Unavailable, []);
            var page = await response.Content.ReadFromJsonAsync<CaseWorkDiscoveryPage>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page is null || page.Access != CaseQueueAccess.Allowed || page.CaseIds is null || page.CaseIds.Count > limit ||
                page.CaseIds.Any(id => id == Guid.Empty || id == cursor) || page.CaseIds.Distinct().Count() != page.CaseIds.Count ||
                page.NextCursor is { } next && (page.CaseIds.Count != limit || next != page.CaseIds[^1]))
                return new(CaseQueueAccess.Unavailable, []);
            return page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(CaseQueueAccess.Unavailable, []); }
    }
}
