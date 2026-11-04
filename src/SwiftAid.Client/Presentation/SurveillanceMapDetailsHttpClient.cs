using System.Net;
using System.Net.Http.Json;
using SwiftAid.Contracts.Surveillance;

namespace SwiftAid.Client.Presentation;

/// <summary>Reads the Gateway's authorized, suppression-checked map projection.</summary>
public sealed class SurveillanceMapDetailsHttpClient(HttpClient httpClient, IWorkspaceAccessClient? workspaces = null) : ISurveillanceMapDetailsClient
{
    private const string Capability = "surveillance-map-details";

    public async Task<UiCapabilityResult<SurveillanceMapDetails>> GetAsync(string countryCode, MapAggregatePeriod period,
        CancellationToken ct = default)
    {
        if (httpClient.BaseAddress is null || countryCode is not { Length: 2 } ||
            !countryCode.All(char.IsAsciiLetter) || !Enum.IsDefined(period)) return Result(UiCapabilityResultCode.Unavailable);
        var address = $"api/v1/surveillance/map-details?country={countryCode.ToUpperInvariant()}&period={(period == MapAggregatePeriod.Days90 ? "days90" : "days30")}";
        try
        {
            if (workspaces is not null)
            {
                var workspace = await workspaces.GetStateAsync(ct).ConfigureAwait(false);
                if (!workspace.Succeeded || workspace.Value?.SelectedWorkspaceId is not { } selected)
                    return Result(workspace.Code == UiCapabilityResultCode.Denied ? UiCapabilityResultCode.Denied : UiCapabilityResultCode.Unavailable);
                address += "&workspace=" + Uri.EscapeDataString(selected);
            }
            using var response = await httpClient.GetAsync(address, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Result(UiCapabilityResultCode.Denied);
            if (!response.IsSuccessStatusCode) return Result(UiCapabilityResultCode.Unavailable);
            var details = await response.Content.ReadFromJsonAsync<SurveillanceMapDetails>(cancellationToken: ct).ConfigureAwait(false);
            if (!IsValid(details)) return Result(UiCapabilityResultCode.Unavailable);
            return Result(UiCapabilityResultCode.Success, details);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Result(UiCapabilityResultCode.Unavailable); }
    }

    private static bool IsValid(SurveillanceMapDetails? details) => details is
        { Points: not null, Signals: not null, UnmappedReports: >= 0 } &&
        details.Points.Count <= 500 && details.Signals.Count <= 10 &&
        details.Points.All(point => point is not null && !string.IsNullOrWhiteSpace(point.Id) &&
            !string.IsNullOrWhiteSpace(point.Label) && double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude) &&
            point.Latitude is >= -90 and <= 90 && point.Longitude is >= -180 and <= 180 &&
            point.HumanReports >= 0 && point.AnimalReports >= 0 &&
            (point.HumanReports == 0 || point.HumanReports >= 5) && (point.AnimalReports == 0 || point.AnimalReports >= 5) &&
            (point.Cases is null || point.Cases.Count <= 500 && point.Cases.All(item => item is not null && item.CaseId != Guid.Empty))) &&
        details.Signals.All(signal => signal is not null && signal.Id != Guid.Empty && signal.Reports >= 0 &&
            signal.CaseHref is { Length: 42 } href && href.StartsWith("/case/", StringComparison.Ordinal) &&
            Guid.TryParseExact(href[6..], "D", out var id) && id != Guid.Empty);

    private static UiCapabilityResult<SurveillanceMapDetails> Result(UiCapabilityResultCode code, SurveillanceMapDetails? value = null) =>
        new(code, value, new(Capability, UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow, code == UiCapabilityResultCode.Success));
}
