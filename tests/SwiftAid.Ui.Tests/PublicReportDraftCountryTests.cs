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
    public void DraftCanPreserveUnverifiedCountryWithoutConfirmingIt()
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

        var restored = PublicReportDraft.FromFormSnapshot(draft.ToFormSnapshot());
        Assert.Equal("zw", restored.CountryCode);
        Assert.False(restored.CountryConfirmed);
        Assert.False(restored.ReportReviewConfirmed);
        Assert.False(restored.ProcessingConfirmed);
        Assert.Throws<InvalidOperationException>(() => draft.ToSubmission(null, "/synthetic-privacy", "policy-v1", [], false));
    }

    [Theory]
    [InlineData("XYZ")]
    [InlineData("Z1")]
    public void DraftRejectsMalformedCountryInsteadOfDiscardingIt(string country)
    {
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human, CountryCode = country };
        Assert.Throws<InvalidOperationException>(() => draft.ToDraftSubmission(null, null, null, [], false, null));
        Assert.Equal(country, draft.CountryCode);
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
