using System.Net;
using System.Net.Http.Json;

namespace SwiftAid.Client.Presentation;

/// <summary>Stores only a display selection; the Gateway rechecks membership on every request.</summary>
public sealed class WorkspaceAccessHttpClient(HttpClient httpClient) : IWorkspaceAccessClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _selected;

    public Task<UiCapabilityResult<WorkspaceAccessState>> GetStateAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(null, false, cancellationToken);

    public Task<UiCapabilityResult<WorkspaceAccessState>> SelectWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) =>
        LoadAsync(workspaceId, true, cancellationToken);

    private async Task<UiCapabilityResult<WorkspaceAccessState>> LoadAsync(string? requested, bool select, CancellationToken ct)
    {
        if (httpClient.BaseAddress is null || select && (string.IsNullOrWhiteSpace(requested) || requested.Length > 256))
            return Result(UiCapabilityResultCode.InvalidRequest);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var selection = select ? requested : _selected;
            var address = "api/v1/me/workspaces" + (selection is null ? "" : "?workspace=" + Uri.EscapeDataString(selection));
            using var response = await httpClient.GetAsync(address, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _selected = null;
                return Result(UiCapabilityResultCode.Denied);
            }
            if (!response.IsSuccessStatusCode) return Result(UiCapabilityResultCode.Unavailable);
            var state = await response.Content.ReadFromJsonAsync<WorkspaceAccessState>(cancellationToken: ct).ConfigureAwait(false);
            if (state?.Workspaces is null || state.Workspaces.Count > 1000 ||
                state.Workspaces.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 256 || string.IsNullOrWhiteSpace(item.Name)) ||
                state.SelectedWorkspaceId is not null && !state.Workspaces.Any(item => item.Id == state.SelectedWorkspaceId) ||
                select && state.SelectedWorkspaceId != requested) return Result(UiCapabilityResultCode.Unavailable);
            _selected = state.SelectedWorkspaceId;
            return Result(UiCapabilityResultCode.Success, state);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Result(UiCapabilityResultCode.Unavailable); }
        finally { _gate.Release(); }
    }

    private static UiCapabilityResult<WorkspaceAccessState> Result(UiCapabilityResultCode code, WorkspaceAccessState? state = null) =>
        new(code, state, new("workspace-display-context", UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow, code == UiCapabilityResultCode.Success));
}
