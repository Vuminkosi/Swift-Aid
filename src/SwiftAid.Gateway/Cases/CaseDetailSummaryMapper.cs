using SwiftAid.Application.Cases;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Gateway.Cases;

/// <summary>
/// Shared projection from the authorized case summary read to the case detail
/// summary view. The operational description is composed from record metadata
/// only and never returns the original report.
/// </summary>
public static class CaseDetailSummaryMapper
{
    public static CaseDetailSummary? ToSummary(
        CaseSummaryData data,
        IReadOnlyDictionary<string, string> countryLabels)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(countryLabels);

        if (data.CaseId == Guid.Empty || string.IsNullOrWhiteSpace(data.CountryCode))
        {
            return null;
        }

        var subjectType = data.Kind == CaseKind.Animal ? CaseWorkspaceSector.Animal : CaseWorkspaceSector.Human;
        var recordedOn = data.CreatedAtUtc.ToString("yyyy-MM-dd");
        var summary = subjectType == CaseWorkspaceSector.Animal
            ? $"Animal report recorded on {recordedOn}, under {data.Workflow}."
            : $"Human case report recorded on {recordedOn}, under {data.Workflow}.";
        if (summary.Length > 500)
        {
            return null;
        }

        var countryLabel = countryLabels.GetValueOrDefault(data.CountryCode) ?? data.CountryCode;
        if (countryLabel.Length > 120)
        {
            countryLabel = data.CountryCode;
        }

        var location = data.RegionIds.Count == 0
            ? countryLabel
            : string.Join(", ", data.RegionIds.Where(region => !string.IsNullOrWhiteSpace(region)));
        if (string.IsNullOrWhiteSpace(location))
        {
            location = countryLabel;
        }

        if (location.Length > 160)
        {
            location = countryLabel;
        }

        var owner = string.IsNullOrWhiteSpace(data.OwnerDisplayName) ? null : data.OwnerDisplayName.Trim();
        if (owner is not null && (owner.Length == 0 || owner.Length > 120))
        {
            owner = null;
        }

        var view = new CaseDetailSummary(
            data.CaseId,
            "Case " + data.CaseId.ToString("N")[..8],
            subjectType,
            summary,
            data.Classification.ToString(),
            data.Workflow.ToString(),
            "Not assessed",
            countryLabel,
            location,
            owner,
            data.CreatedAtUtc,
            data.UpdatedAtUtc);
        return view.IsValid ? view : null;
    }
}
