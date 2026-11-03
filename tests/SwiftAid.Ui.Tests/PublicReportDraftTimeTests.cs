using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Components.Public;

namespace SwiftAid.Ui.Tests;

public sealed class PublicReportDraftTimeTests
{
    private static PublicReportDraft Draft() => new()
    {
        SubjectScope = IntakeSubjectScope.Human,
        Narrative = "Synthetic observation",
        CapturedAt = new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.FromHours(2)),
        OccurrencePrecision = ReportedTimePrecision.Unknown,
        CountryCode = "ZW",
        CountryConfirmed = true,
        ReportReviewConfirmed = true,
        ProcessingConfirmed = true
    };

    private static IntakeSubmission Submit(PublicReportDraft draft) => draft.ToSubmission(
        "synthetic-countries-v1",
        "/synthetic-privacy",
        "synthetic-policy-v1",
        [new PublicCountryOption("ZW", "Zimbabwe")],
        countryCatalogueIsCurrent: true);

    [Fact]
    public void ToSubmission_ExactCaptureTime_EmitsInstantOnlyWithSignedOffset()
    {
        var submission = Submit(Draft());

        Assert.Equal(ReportedTimePrecision.Exact, submission.CapturedAt.Precision);
        Assert.NotNull(submission.CapturedAt.Value);
        Assert.Null(submission.CapturedAt.LocalDate);
        Assert.Null(submission.CapturedAt.RangeStart);
        Assert.Null(submission.CapturedAt.RangeEnd);
        Assert.Matches(@"^[+-]\d{2}:\d{2}$", submission.CapturedAt.OriginalOffset ?? string.Empty);
    }

    [Fact]
    public void ToSubmission_UnknownOccurrence_EmitsEmptyUnknown()
    {
        var submission = Submit(Draft());

        Assert.Equal(ReportedTimePrecision.Unknown, submission.Occurrence.Precision);
        Assert.Null(submission.Occurrence.Value);
        Assert.Null(submission.Occurrence.LocalDate);
    }

    [Fact]
    public void ToSubmission_ExactOccurrence_EmitsInstantOnlyWithSignedOffset()
    {
        var draft = Draft();
        draft.OccurrencePrecision = ReportedTimePrecision.Exact;
        draft.OccurrenceDate = new DateTime(2026, 10, 31);
        draft.OccurrenceTime = "08:15";
        draft.OccurrenceOffset = "+02:00";

        var submission = Submit(draft);

        Assert.Equal(ReportedTimePrecision.Exact, submission.Occurrence.Precision);
        Assert.NotNull(submission.Occurrence.Value);
        Assert.Null(submission.Occurrence.LocalDate);
        Assert.Equal("+02:00", submission.Occurrence.OriginalOffset);
    }

    [Theory]
    [InlineData("+-1:00")]
    [InlineData("+ 2:00")]
    [InlineData("+02:-1")]
    [InlineData("+14:01")]
    public void ToSubmission_RejectsMalformedExactOffset(string offset)
    {
        var draft = Draft();
        draft.OccurrencePrecision = ReportedTimePrecision.Exact;
        draft.OccurrenceDate = new DateTime(2026, 10, 31);
        draft.OccurrenceTime = "08:15";
        draft.OccurrenceOffset = offset;

        Assert.Throws<InvalidOperationException>(() => Submit(draft));
    }

    [Fact]
    public void ToSubmission_RejectsOversizedKnownPlaceAndAcceptsBoundary()
    {
        var draft = Draft();
        draft.RegionPresence = PresenceState.Known;
        draft.Region = new string('x', 201);
        Assert.Throws<InvalidOperationException>(() => Submit(draft));

        draft.Region = new string('x', 200);
        Assert.Equal(draft.Region, Submit(draft).Location.Region.ReportedName);
    }
}
