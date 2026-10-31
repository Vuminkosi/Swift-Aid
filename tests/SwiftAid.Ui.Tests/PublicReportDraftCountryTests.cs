using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Components.Public;

namespace SwiftAid.Ui.Tests;

public sealed class PublicReportDraftCountryTests
{
    [Fact]
    public void ToSubmission_CaseInsensitiveCurrentCountry_UsesCatalogueCode()
    {
        var draft = new PublicReportDraft
        {
            SubjectScope = IntakeSubjectScope.Human,
            Narrative = "Synthetic observation",
            CountryCode = "zw",
            CountryConfirmed = true,
            ReportReviewConfirmed = true,
            ProcessingConfirmed = true
        };

        var submission = draft.ToSubmission(
            "synthetic-countries-v1",
            "/synthetic-privacy",
            "synthetic-policy-v1",
            [new PublicCountryOption("ZW", "Zimbabwe")],
            countryCatalogueIsCurrent: true);

        Assert.Equal("ZW", submission.Location.CountryCode);
    }

    [Fact]
    public void ToSubmission_StaleCountryOutsideCurrentCatalogue_IsRejected()
    {
        var draft = new PublicReportDraft
        {
            SubjectScope = IntakeSubjectScope.Human,
            Narrative = "Synthetic observation",
            CountryCode = "XX",
            CountryConfirmed = true,
            ReportReviewConfirmed = true,
            ProcessingConfirmed = true
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => draft.ToSubmission(
                "synthetic-countries-v1",
                "/synthetic-privacy",
                "synthetic-policy-v1",
                [new PublicCountryOption("ZW", "Zimbabwe")],
                countryCatalogueIsCurrent: true));

        Assert.Contains("not in the current country list", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
