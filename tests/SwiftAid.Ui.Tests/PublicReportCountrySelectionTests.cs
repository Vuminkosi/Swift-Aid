using System.Reflection;
using SwiftAid.UI.Components.Public;
using SwiftAid.UI.Components.Surfaces.Public;

namespace SwiftAid.Ui.Tests;

public sealed class PublicReportCountrySelectionTests
{
    [Fact]
    public void SelectionIncludesSadcMembersAndPreservesAnExistingCountry()
    {
        string[] members = ["AO", "BW", "KM", "CD", "SZ", "LS", "MG", "MW",
            "MU", "MZ", "NA", "SC", "ZA", "TZ", "ZM", "ZW"];
        var surface = new PublicReportSurface
        {
            Draft = new PublicReportDraft { CountryCode = "GB" },
            Countries = members.Concat(["GB", "US"])
                .Select(code => new PublicCountryOption(code, code)).ToArray()
        };

        var countries = (IReadOnlyList<PublicCountryOption>)typeof(PublicReportSurface)
            .GetProperty("ReportCountries", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(surface)!;

        Assert.Equal(17, countries.Count);
        Assert.All(members, code => Assert.Contains(countries, country => country.Code == code));
        Assert.Contains(countries, country => country.Code == "GB");
        Assert.DoesNotContain(countries, country => country.Code == "US");
    }
}
