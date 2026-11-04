using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Components.Public;

namespace SwiftAid.Ui.Tests;

public sealed class PublicReportOfflineDraftTests
{
    [Fact]
    public void ExistingCountryIsPreservedForLocalSaveWithoutRenewingConfirmation()
    {
        var draft = NewDraft();
        var original = draft.ToSubmission("countries-v1", "/privacy", "v1", [new("SZ", "Eswatini")], true)
            with { ClientSubmissionId = Guid.NewGuid() };
        var reopened = PublicReportDraft.FromSubmission(original);
        reopened.Narrative = "Edited offline";

        var saved = reopened.ToDraftSubmission(null, null, null, [], false, original);

        Assert.Equal("Edited offline", saved.Narrative);
        Assert.Equal("SZ", saved.Location.CountryCode);
        Assert.Equal("countries-v1", saved.Location.CatalogueVersion);
        Assert.False(saved.Confirmations.OccurrenceCountryConfirmed);
        Assert.False(saved.Confirmations.ProcessingConfirmed);
        Assert.Throws<InvalidOperationException>(() => reopened.ToSubmission(null, null, null, [], false));
    }

    [Fact]
    public void OfflineSaveCannotInventOrReplaceCountry()
    {
        var draft = NewDraft();
        var original = draft.ToSubmission("countries-v1", "/privacy", "v1", [new("SZ", "Eswatini")], true);
        draft.CountryCode = "ZA";
        Assert.Throws<InvalidOperationException>(() => draft.ToDraftSubmission(null, null, null, [], false, original));
        draft.CountryCode = "SZ";
        Assert.Throws<InvalidOperationException>(() => draft.ToDraftSubmission(null, null, null, [], false, null));
    }

    [Fact]
    public void RemovedCountryRemainsEditableButRequiresNewConfirmation()
    {
        var draft = NewDraft();
        var original = draft.ToSubmission("countries-v1", "/privacy", "v1", [new("SZ", "Eswatini")], true);
        var saved = draft.ToDraftSubmission("countries-v2", "/privacy", "v1", [new("ZA", "South Africa")], true, original);
        Assert.Equal("SZ", saved.Location.CountryCode);
        Assert.Equal("countries-v1", saved.Location.CatalogueVersion);
        Assert.False(saved.Confirmations.OccurrenceCountryConfirmed);
        Assert.Throws<InvalidOperationException>(() => draft.ToSubmission("countries-v2", "/privacy", "v1", [new("ZA", "South Africa")], true));
    }

    private static PublicReportDraft NewDraft() => new()
    {
        SubjectScope = IntakeSubjectScope.Human, Narrative = "Synthetic report", CountryCode = "SZ",
        CountryConfirmed = true, ReportReviewConfirmed = true, ProcessingConfirmed = true
    };
}
