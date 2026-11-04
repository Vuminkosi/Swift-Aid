using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Presentation;

public interface ICaseReportDetailsClient
{
    Task<UiCapabilityResult<CaseReportDetails>> GetAsync(Guid caseId, CancellationToken ct = default);
}

public static class CaseReportDisplay
{
    public static string LocationLabel(CaseReportPlace place)
    {
        var region = place.Region ?? "Region not recorded";
        var area = place.Locality ?? place.Area ?? "Area not recorded";
        return $"{Shorten(region, 45)} | {Shorten(area, 45)}";
    }

    private static string Shorten(string value, int limit) => value.Length <= limit ? value : value[..(limit - 1)] + "\u2026";

    public static string MapHref(CaseReportPlace place) =>
        "/surveillance?country=" + Uri.EscapeDataString(place.CountryCode) +
        "&place=" + Uri.EscapeDataString(place.LocalityId ?? place.AreaId ?? place.RegionId ?? "") +
        "&region=" + Uri.EscapeDataString(place.Region ?? "") +
        "&area=" + Uri.EscapeDataString(place.Locality ?? place.Area ?? "");

    public static string ObservationPreview(CaseReportDetails report)
    {
        var text = string.Join("; ", report.Observations.Take(3));
        if (string.IsNullOrWhiteSpace(text)) text = report.Narrative ?? "No observations recorded";
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 180 ? text : text[..177] + "…";
    }
}
