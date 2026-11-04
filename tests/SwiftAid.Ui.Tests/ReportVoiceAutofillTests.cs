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
