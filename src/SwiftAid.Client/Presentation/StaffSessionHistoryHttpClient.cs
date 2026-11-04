using System.Net;
using System.Net.Http.Json;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Client.Presentation;

/// <summary>Reads one authorized, bounded session-history page from the Gateway.</summary>
public sealed class StaffSessionHistoryHttpClient(HttpClient httpClient) : IStaffSessionHistoryClient
{
    private const string Capability = "community-session-history";
    private const int PageSize = 25;

    public async Task<UiCapabilityResult<SessionHistoryPage>> ReadAsync(Guid sessionId, long? beforeVersion,
        CancellationToken cancellationToken = default)
    {
        if (httpClient.BaseAddress is null) return Result(UiCapabilityResultCode.Unavailable);
        if (sessionId == Guid.Empty || beforeVersion is <= 0) return Result(UiCapabilityResultCode.InvalidRequest);
        var path = $"api/v1/sessions/{sessionId:D}/history?limit={PageSize}";
        if (beforeVersion.HasValue) path += $"&beforeVersion={beforeVersion.Value}";
        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return Result(UiCapabilityResultCode.Denied);
            if (!response.IsSuccessStatusCode) return Result(UiCapabilityResultCode.Unavailable);
            var page = await response.Content.ReadFromJsonAsync<SessionHistoryPage>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!IsValid(page, sessionId, beforeVersion)) return Result(UiCapabilityResultCode.Unavailable);
            return Result(UiCapabilityResultCode.Success, page);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Result(UiCapabilityResultCode.Unavailable); }
    }

    private static bool IsValid(SessionHistoryPage? page, Guid sessionId, long? before)
    {
        if (page?.SessionId != sessionId || page.Items is null || page.Items.Count > PageSize ||
            page.BeforeVersion is not null && (page.Items.Count == 0 || page.BeforeVersion != page.Items[^1]?.Version)) return false;
        var previous = before;
        foreach (var entry in page.Items)
        {
            if (entry is null || entry.Version <= 0 || previous is not null && entry.Version >= previous) return false;
            previous = entry.Version;
        }
        return true;
    }

    private static UiCapabilityResult<SessionHistoryPage> Result(UiCapabilityResultCode code, SessionHistoryPage? page = null) =>
        new(code, page, new(Capability, UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow, code == UiCapabilityResultCode.Success));
}
