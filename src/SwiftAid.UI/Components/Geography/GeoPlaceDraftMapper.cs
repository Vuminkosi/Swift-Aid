using SwiftAid.Contracts.Geography;
using SwiftAid.UI.Components.Public;

namespace SwiftAid.UI.Components.Geography;

/// <summary>Applies registry place selections to a public report draft. Pure logic; the stepper owns notifications.</summary>
public static class GeoPlaceDraftMapper
{
    /// <summary>Returns true when the option's country is in the current catalogue.</summary>
    public static bool IsSupportedCountry(GeoPlaceOption option, IReadOnlyList<PublicCountryOption> countries) =>
        countries.Any(country =>
            string.Equals(country.Code, option.CountryCode?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static void ApplySelection(
        PublicReportDraft draft,
        GeoPlaceOption? option,
        IReadOnlyList<PublicCountryOption> countries)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(countries);
        if (option is null)
        {
            ClearSubCountryPlace(draft);
            draft.LocationUnknown = string.IsNullOrWhiteSpace(draft.CountryCode);
            return;
        }

        var supported = countries.FirstOrDefault(country =>
            string.Equals(country.Code, option.CountryCode?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (supported is not null)
        {
            draft.CountryCode = supported.Code;
        }

        draft.CountryConfirmed = false;
        draft.LocationUnknown = string.IsNullOrWhiteSpace(draft.CountryCode);

        string? regionName = null;
        string? regionId = null;
        if (option.KindLabel is "Region" or "Province")
        {
            regionName = option.Name;
            regionId = option.Id;
        }
        else
        {
            var regionAncestor = option.Ancestors.LastOrDefault(ancestor => ancestor.KindLabel is "Region" or "Province");
            if (regionAncestor is not null)
            {
                regionName = regionAncestor.Name;
                regionId = regionAncestor.Id;
            }
        }

        if (regionName is not null)
        {
            draft.Region = regionName;
            draft.RegionId = regionId;
            draft.RegionPresence = PresenceState.Known;
        }
        else
        {
            draft.Region = null;
            draft.RegionId = null;
            draft.RegionPresence = PresenceState.Unknown;
        }

        if (option.KindLabel == "Settlement")
        {
            draft.Locality = option.Name;
            draft.LocalityId = option.Id;
            draft.LocalityPresence = PresenceState.Known;
        }
        else
        {
            draft.Locality = null;
            draft.LocalityId = null;
            draft.LocalityPresence = PresenceState.Unknown;
        }
    }

    public static void ClearSubCountryPlace(PublicReportDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.Region = null;
        draft.RegionId = null;
        draft.RegionPresence = PresenceState.Unknown;
        draft.Area = null;
        draft.AreaId = null;
        draft.AreaPresence = PresenceState.Unknown;
        draft.Locality = null;
        draft.LocalityId = null;
        draft.LocalityPresence = PresenceState.Unknown;
    }
}
