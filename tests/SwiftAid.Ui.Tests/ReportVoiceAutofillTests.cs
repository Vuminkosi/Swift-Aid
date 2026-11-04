using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Components.Public;

namespace SwiftAid.Ui.Tests;

public sealed class ReportVoiceAutofillTests
{
    [Theory]
    [InlineData("age", "35", "The patient is 35 years old", "The patient is 35 years old.", true)]
    [InlineData("age", "35", "35 years old", "The patient is not 35 years old.", false)]
    [InlineData("locality", "Mbabane", "She lives in Mbabane", "She lives in Mbabane.", true)]
    [InlineData("locality", "Mbabane", "Mbabane", "She does not live in Mbabane.", false)]
    [InlineData("observation", "fever", "No fever", "No fever.", true)]
    [InlineData("observation", "fever", "fever", "No fever.", false)]
    public void OnlyCompleteDirectStatementsCanAutofill(string field, string value, string evidence, string transcript, bool expected)
    {
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human };
        Assert.Equal(expected, ReportVoiceAutofill.CanApply(new(field, value, evidence), transcript, draft, _ => false));
    }

    [Theory]
    [InlineData("age", "35", "The patient is 35 years old", "The patient is 35 years old. She lives in Mbabane.", true)]
    [InlineData("age", "35 years old", "The patient is is 35 years old", "The patient is is 35 years old. She lives in Mbabane.", true)]
    [InlineData("age", "35", "The patient patient is 35 years old", "The patient patient is 35 years old. She lives in Mbabane.", true)]
    [InlineData("locality", "Mbabane", "She lives in Mbabane", "The patient is 35 years old. She lives in Mbabane.", true)]
    [InlineData("observation", "fever", "No fever", "No fever. She lives in Mbabane.", true)]
    [InlineData("age", "35", "The patient is 35 years old", "The patient is 35 years old. The patient is 36 years old.", false)]
    [InlineData("locality", "Mbabane", "She lives in Mbabane", "She lives in Mbabane. She lives in Manzini.", false)]
    [InlineData("age", "35", "35 years old", "The patient is not 35 years old. She lives in Mbabane.", false)]
    [InlineData("age", "35", "The patient is 35 years old", "The patient is 35 years old? She lives in Mbabane.", false)]
    [InlineData("locality", "Mbabane", "She lives in Mbabane", "She lives in Mbabane. She does not live in Mbabane.", false)]
    [InlineData("age", "35", "The patient is 35 years old", "The patient is 35 years old. Actually, she is 36.", false)]
    public void CompleteSentencesCanAutofillWithinLongerReports(string field, string value, string evidence, string transcript, bool expected)
    {
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human };
        Assert.Equal(expected, ReportVoiceAutofill.CanApply(new(field, value, evidence), transcript, draft, _ => false));
    }

    [Fact]
    public void ExplicitCountryMustMatchCurrentCatalogueAndUntouchedField()
    {
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human };
        var countries = new[] { new PublicCountryOption("SZ", "Eswatini"), new PublicCountryOption("ZA", "South Africa") };
        var suggestion = new ReportVoiceSuggestion("country", "Eswatini", "This happened in Eswatini");
        const string transcript = "This happened in Eswatini. The patient is 35 years old.";
        Assert.True(ReportVoiceAutofill.TryGetCountryCode(suggestion, transcript, draft, _ => false, countries, out var code));
        Assert.Equal("SZ", code);
        var duplicated = new ReportVoiceSuggestion("country", "Eswatini", "This happened happened in Eswatini");
        Assert.True(ReportVoiceAutofill.TryGetCountryCode(duplicated, "This happened happened in Eswatini.", draft, _ => false, countries, out _));
        Assert.False(ReportVoiceAutofill.TryGetCountryCode(suggestion, transcript, draft, _ => true, countries, out _));
        Assert.False(ReportVoiceAutofill.TryGetCountryCode(suggestion, transcript, draft, _ => false, [], out _));
        Assert.False(ReportVoiceAutofill.TryGetCountryCode(suggestion,
            "This happened in Eswatini. Actually, it happened in South Africa.", draft, _ => false, countries, out _));
        draft.CountryCode = "ZA";
        Assert.False(ReportVoiceAutofill.TryGetCountryCode(suggestion, transcript, draft, _ => false, countries, out _));
    }

    [Theory]
    [InlineData("age", "35", true)]
    [InlineData("locality", "Lusaka", true)]
    [InlineData("country", "Zambia", true)]
    public void SpokenCommaSeparatedClausesRetainSourceContext(string field, string value, bool expected)
    {
        const string transcript = "This is a synthetic test, the patient is 35 years old, she lives in lives in Lusaka, this happened in Zambia.";
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human };
        Assert.Equal(expected, ReportVoiceAutofill.CanApply(new(field, value, transcript), transcript, draft, _ => false));
    }

    [Theory]
    [InlineData("Not, the patient is 35 years old.")]
    [InlineData("The patient is 35 years old, she is not 35.")]
    [InlineData("The patient is 35 years old, actually she is 36.")]
    [InlineData("The patient is 35 years old, the patient is 36 years old.")]
    public void NegationAndCompetingCommaSeparatedClausesAreNotAutoApplied(string transcript)
    {
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human };
        Assert.False(ReportVoiceAutofill.CanApply(new("age", "35", transcript), transcript, draft, _ => false));
    }

    [Theory]
    [InlineData("age", "35")]
    [InlineData("locality", "Lusaka")]
    [InlineData("country", "Zambia")]
    public void UnpunctuatedAsrStatementsCanBeAppliedWhenDirect(string field, string value)
    {
        const string transcript = "This is a synthetic test the patient is 35 years old she lives in Lusaka this happened in Zambia";
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human };
        Assert.True(ReportVoiceAutofill.CanApply(new(field, value, transcript), transcript, draft, _ => false));
    }

    [Theory]
    [InlineData("Not the patient is 35 years old she lives in Lusaka")]
    [InlineData("Maybe the patient is 35 years old she lives in Lusaka")]
    [InlineData("I don't know if the patient is 35 years old she lives in Lusaka")]
    [InlineData("I think the patient is 35 years old she lives in Lusaka")]
    [InlineData("I am not sure whether the patient is 35 years old she lives in Lusaka")]
    [InlineData("The patient is 35 years old she is not 35 years old")]
    public void UnpunctuatedNegationAndUncertaintyAreNotAutoApplied(string transcript)
    {
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human };
        Assert.False(ReportVoiceAutofill.CanApply(new("age", "35", transcript), transcript, draft, _ => false));
    }

    [Fact]
    public void ExistingAndUserEditedFieldsAreNeverOverwritten()
    {
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human, AgeYearsText = "40" };
        var age = new ReportVoiceSuggestion("age", "35", "The patient is 35 years old");
        Assert.False(ReportVoiceAutofill.CanApply(age, "The patient is 35 years old.", draft, _ => false));
        draft.AgeYearsText = null;
        Assert.False(ReportVoiceAutofill.CanApply(age, "The patient is 35 years old.", draft,
            field => field == nameof(PublicReportDraft.AgeYearsText)));
    }
}
