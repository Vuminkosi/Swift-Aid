using SwiftAid.Contracts.Geography;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Domain.Tests;

public sealed class ReportValidationTests
{
    private static readonly ReportProvenance NarrativeSource = new(ReportSourceKind.ReporterNarrative, "narrative:1");
    private static readonly ReportProvenance ObservationSource = new(ReportSourceKind.ReporterObservation, "observation:1");
    private static readonly ReportProvenance AudioSource = new(ReportSourceKind.OriginalAudio, "audio:pending:1");

    [Fact]
    public void MinimalNarrativeOnlyReport_AcceptsUnknownFieldsAndPreservesProvenance()
    {
        var report = HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.NotRecorded(NarrativeSource),
            ReportedFact<string>.Known("worker-reported concern; cause not known", NarrativeSource),
            new ReportEvidence(
                new ReportedText("A person reported feeling unwell.", NarrativeSource),
                [],
                null));

        Assert.Equal(ReportedValueState.Unknown, report.Context.Occurrence.Value.State);
        Assert.Equal(NarrativeSource, report.Context.Occurrence.Provenance);
        Assert.Equal(ReportedValueState.NotRecorded, report.AgeYears.Value.State);
        Assert.Equal("worker-reported concern; cause not known", report.ReportedConcern.Value.Value);
        Assert.Equal("A person reported feeling unwell.", report.Evidence.Narrative!.Text);
        Assert.Equal(NarrativeSource, report.Evidence.Narrative.Provenance);
        Assert.Equal(TimeSpan.Zero, report.Context.CapturedAtUtc!.Value.Offset);
        Assert.Equal("+02:00", report.Context.CapturedOffset);
    }

    [Fact]
    public void EmptyReportWithoutObservationNarrativeOrAudio_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportEvidence.Empty));
    }

    [Fact]
    public void MissingGps_PreservesDeclaredUnknownLocationInsteadOfInventingDeviceLocation()
    {
        var report = HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown, LocationWithoutCoordinates()),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            new ReportEvidence(null, [new ReportedText("Reported observation.", ObservationSource)], null));

        Assert.Equal(LocationSource.Reporter, report.Context.Location.Source);
        Assert.Equal(LocationPrecision.Unknown, report.Context.Location.Precision);
        Assert.Equal(PresenceState.Unknown, report.Context.Location.Locality.Presence);
        Assert.Null(report.Context.Location.Latitude);
        Assert.Null(report.Context.Location.Longitude);
    }

    [Fact]
    public void PendingAudioReference_IsPreservedAsOpaqueEvidenceWithoutUploadClaim()
    {
        var report = HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown),
            ReportedFact<string>.Unknown(AudioSource),
            ReportedFact<int>.Unknown(AudioSource),
            ReportedFact<string>.Unknown(AudioSource),
            new ReportEvidence(null, [], new ReportEvidenceReference("audio:pending:1", AudioSource)));

        Assert.Equal("audio:pending:1", report.Evidence.OriginalAudio!.Reference);
        Assert.Equal(AudioSource, report.Evidence.OriginalAudio.Provenance);
        Assert.Equal(ReportSourceKind.OriginalAudio, report.Evidence.OriginalAudio.Provenance.Source);
    }

    [Fact]
    public void HumanNarrativeMentioningMultiplePeople_RemainsOneReportWithOriginalText()
    {
        const string narrative = "Three people from the same household reported feeling unwell.";
        var reportId = Guid.NewGuid();
        var report = HumanReport.Create(
            reportId,
            Context(ReportedValue<ReportedTime>.Unknown),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            new ReportEvidence(new ReportedText(narrative, NarrativeSource), [], null));

        Assert.Equal(narrative, report.Evidence.Narrative!.Text);
        Assert.Equal(reportId, report.ReportId);
    }

    [Fact]
    public void ReportedConcernDoesNotSetClinicalClassification()
    {
        var report = HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Known("Cholera was mentioned; no test result recorded.", NarrativeSource),
            new ReportEvidence(new ReportedText("The reporter mentioned cholera.", NarrativeSource), [], null));

        Assert.Equal("Cholera was mentioned; no test result recorded.", report.ReportedConcern.Value.Value);
        Assert.Null(typeof(HumanReport).GetProperty("Classification"));
    }

    [Fact]
    public void HumanReport_RequiresExplicitContext()
    {
        Assert.Throws<ArgumentNullException>(() => HumanReport.Create(
            Guid.NewGuid(),
            null!,
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            new ReportEvidence(new ReportedText("Narrative.", NarrativeSource), [], null)));
    }

    [Fact]
    public void ReportContext_RejectsUndefinedCaptureAndOccurrencePrecision()
    {
        var evidence = new ReportEvidence(new ReportedText("Narrative.", NarrativeSource), [], null);

        Assert.Throws<ArgumentOutOfRangeException>(() => HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown, capturePrecision: (ReportedTimePrecision)int.MaxValue),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            evidence));

        Assert.Throws<ArgumentOutOfRangeException>(() => HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Known(ReportedTimeFor((ReportedTimePrecision)int.MaxValue, new DateOnly(2026, 10, 20)))),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            evidence));
    }

    [Fact]
    public void ReportedTime_ExactInstantIsStoredInUtcAndRetainsOriginalOffsetAndZone()
    {
        var sourceInstant = DateTimeOffset.Parse("2026-10-20T08:15:00+02:00");
        var reported = new ReportedTime(sourceInstant, null, null, null, "+02:00", "Africa/Mbabane", ReportedTimePrecision.Exact);
        var normalized = ReportedTimeRules.Normalize(reported);

        Assert.Equal(TimeSpan.Zero, normalized.Value!.Value.Offset);
        Assert.Equal(sourceInstant.UtcDateTime, normalized.Value.Value.UtcDateTime);
        Assert.Equal("+02:00", normalized.OriginalOffset);
        Assert.Equal("Africa/Mbabane", normalized.ZoneId);
    }

    [Fact]
    public void ReportedTime_DateOnlyAndApproximateValuesArePreserved()
    {
        var localDate = new DateOnly(2026, 10, 20);
        var dateOnly = ReportedTimeRules.Normalize(new ReportedTime(null, localDate, null, null, null, null, ReportedTimePrecision.DateOnly));
        var approximateRange = ReportedTimeRules.Normalize(new ReportedTime(null, null, localDate, localDate.AddDays(2), null, null, ReportedTimePrecision.Approximate));
        var approximateDate = ReportedTimeRules.Normalize(new ReportedTime(null, localDate, null, null, null, null, ReportedTimePrecision.Approximate));
        var approximateInstant = ReportedTimeRules.Normalize(new ReportedTime(
            DateTimeOffset.Parse("2026-10-20T08:15:00+02:00"), null, null, null, "+02:00", null,
            ReportedTimePrecision.Approximate));

        Assert.Equal(localDate, dateOnly.LocalDate);
        Assert.Equal(localDate, approximateRange.RangeStart);
        Assert.Equal(localDate.AddDays(2), approximateRange.RangeEnd);
        Assert.Equal(localDate, approximateDate.LocalDate);
        Assert.Null(approximateDate.Value);
        Assert.Equal(TimeSpan.Zero, approximateInstant.Value!.Value.Offset);
        Assert.Equal("+02:00", approximateInstant.OriginalOffset);
    }

    [Fact]
    public void ReportedTime_UnknownPrecisionDoesNotInventAnInstantOrDate()
    {
        var unknown = ReportedTimeRules.Normalize(new ReportedTime(null, null, null, null, null, null, ReportedTimePrecision.Unknown));

        Assert.Null(unknown.Value);
        Assert.Null(unknown.LocalDate);
        Assert.Null(unknown.RangeStart);
        Assert.Null(unknown.RangeEnd);
        Assert.Equal(ReportedTimePrecision.Unknown, unknown.Precision);
    }

    [Fact]
    public void ReportedTime_RejectsOffsetContradictingSuppliedZoneAtInstant()
    {
        var reportedTime = new ReportedTime(DateTimeOffset.Parse("2026-10-20T08:15:00+02:00"), null, null, null,
            "+00:00", "Africa/Mbabane", ReportedTimePrecision.Exact);

        Assert.Throws<ArgumentException>(() => ReportedTimeRules.Validate(reportedTime));
    }

    [Fact]
    public void ReportedTime_RejectsMissingCompetingHalfAndReversedRepresentations()
    {
        var instant = DateTimeOffset.Parse("2026-10-20T08:15:00+02:00");
        var day = new DateOnly(2026, 10, 20);
        var invalid = new[]
        {
            new ReportedTime(null, null, null, null, null, null, ReportedTimePrecision.Exact),
            new ReportedTime(instant, day, null, null, null, null, ReportedTimePrecision.Exact),
            new ReportedTime(null, null, null, null, null, null, ReportedTimePrecision.DateOnly),
            new ReportedTime(instant, day, null, null, null, null, ReportedTimePrecision.DateOnly),
            new ReportedTime(null, null, null, null, null, null, ReportedTimePrecision.Approximate),
            new ReportedTime(null, null, day, null, null, null, ReportedTimePrecision.Approximate),
            new ReportedTime(instant, day, null, null, null, null, ReportedTimePrecision.Approximate),
            new ReportedTime(null, null, day.AddDays(1), day, null, null, ReportedTimePrecision.Approximate),
            new ReportedTime(instant, null, null, null, null, null, ReportedTimePrecision.Unknown),
            new ReportedTime(null, day, null, null, null, null, ReportedTimePrecision.Unknown)
        };

        foreach (var reportedTime in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => ReportedTimeRules.Validate(reportedTime));
        }
    }

    [Fact]
    public void AnimalObservationPeriod_NormalizesKnownInstantsAndUsesSharedTimeRules()
    {
        var instant = DateTimeOffset.Parse("2026-10-20T08:15:00+02:00");
        var period = ReportedFact<ReportedTime>.Known(
            new ReportedTime(instant, null, null, null, "+02:00", "Africa/Mbabane", ReportedTimePrecision.Exact),
            ObservationSource);
        var count = AnimalCount.Known(3, ObservationSource, "herd:1", period);

        var normalizedInstant = count.ObservationPeriod!.Value.Value.Value;
        Assert.True(normalizedInstant.HasValue);
        Assert.Equal(TimeSpan.Zero, normalizedInstant.Value.Offset);
        Assert.Equal("+02:00", count.ObservationPeriod.Value.Value.OriginalOffset);

        Assert.ThrowsAny<ArgumentException>(() => AnimalCount.Known(3, ObservationSource, "herd:1",
            ReportedFact<ReportedTime>.Known(new ReportedTime(null, null, new DateOnly(2026, 10, 21), null,
                null, null, ReportedTimePrecision.Approximate), ObservationSource)));
    }

    [Fact]
    public void ReportContext_PreservesAbsentCaptureMetadataWithoutDefaults()
    {
        var report = HumanReport.Create(
            Guid.NewGuid(),
            Context(
                ReportedValue<ReportedTime>.Unknown,
                capturePrecision: ReportedTimePrecision.Unknown,
                capturedOffset: null,
                capturedZoneId: null,
                includeCaptureTime: false),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            new ReportEvidence(new ReportedText("Narrative.", NarrativeSource), [], null));

        Assert.Null(report.Context.CapturedAtUtc);
        Assert.Null(report.Context.CapturedOffset);
        Assert.Null(report.Context.CapturedZoneId);
        Assert.Equal(ReportedTimePrecision.Unknown, report.Context.CapturePrecision);
    }

    [Fact]
    public void ReportContext_PreservesValidBoundaryOffsetWithoutCaptureInstantOrZone()
    {
        var report = HumanReport.Create(
            Guid.NewGuid(),
            Context(
                ReportedValue<ReportedTime>.Unknown,
                capturePrecision: ReportedTimePrecision.Unknown,
                capturedOffset: "+14:00",
                capturedZoneId: null,
                includeCaptureTime: false),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            new ReportEvidence(new ReportedText("Narrative.", NarrativeSource), [], null));

        Assert.Null(report.Context.CapturedAtUtc);
        Assert.Equal("+14:00", report.Context.CapturedOffset);
        Assert.Null(report.Context.CapturedZoneId);
        Assert.Equal(ReportedTimePrecision.Unknown, report.Context.CapturePrecision);
    }

    [Theory]
    [InlineData("2:00")]
    [InlineData("+2:00")]
    [InlineData("+14:01")]
    [InlineData("-15:00")]
    public void ReportContext_RejectsMalformedOrOutOfRangeCaptureOffsets(string capturedOffset)
    {
        Assert.ThrowsAny<ArgumentException>(() => HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown, capturedOffset: capturedOffset),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            new ReportEvidence(new ReportedText("Narrative.", NarrativeSource), [], null)));
    }

    [Fact]
    public void ReportContext_RejectsInvalidZoneAndOffsetContradictingKnownZone()
    {
        var evidence = new ReportEvidence(new ReportedText("Narrative.", NarrativeSource), [], null);

        Assert.Throws<ArgumentException>(() => HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown, capturedZoneId: "Not/A_Real_Zone"),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            evidence));

        Assert.Throws<ArgumentException>(() => HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown, capturedOffset: "+02:00", capturedZoneId: "UTC"),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            evidence));
    }

    [Theory]
    [InlineData(double.NaN, null)]
    [InlineData(null, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, null)]
    public void ReportContext_RejectsNonFiniteCoordinates(double? latitude, double? longitude)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown, LocationWithoutCoordinates(latitude, longitude)),
            ReportedFact<string>.Unknown(NarrativeSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            new ReportEvidence(new ReportedText("Narrative.", NarrativeSource), [], null)));
    }

    [Fact]
    public void ReportProvenance_RejectsUndefinedSourceKind()
    {
        var invalidSource = new ReportProvenance((ReportSourceKind)int.MaxValue, "field:1");

        Assert.Throws<ArgumentOutOfRangeException>(() => HumanReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown),
            ReportedFact<string>.Unknown(invalidSource),
            ReportedFact<int>.Unknown(NarrativeSource),
            ReportedFact<string>.Unknown(NarrativeSource),
            new ReportEvidence(new ReportedText("Narrative.", NarrativeSource), [], null)));
    }

    [Fact]
    public void AnimalReport_KeepsReportAndAnimalCountsSeparate_AndRequestsCorrectionForComparableContradiction()
    {
        var period = ReportedFact<ReportedTime>.Known(ReportedTimeFor(ReportedTimePrecision.DateOnly, new DateOnly(2026, 10, 20)), ObservationSource);
        var reportId = Guid.NewGuid();
        var report = AnimalReport.Create(
            reportId,
            Context(ReportedValue<ReportedTime>.Unknown),
            AnimalSubjectScope.Herd,
            ReportedFact<string>.Unknown(ObservationSource),
            ReportedFact<string>.Known("holding:synthetic-1", ObservationSource),
            new ReportEvidence(null, [new ReportedText("Several animals were observed.", ObservationSource)], null),
            AnimalCount.Known(10, ObservationSource, "herd:synthetic-1", period),
            AnimalCount.Known(3, ObservationSource, "herd:synthetic-1", period),
            AnimalCount.Known(4, ObservationSource, "herd:synthetic-1", period));

        var issue = Assert.Single(report.Validate());
        Assert.Equal(ReportValidationIssueKind.CountRelationshipNeedsCorrection, issue.Kind);
        Assert.Equal("reported-dead", issue.Field);
        Assert.Equal(ReportedValueState.Known, report.AnimalsAtRisk.Value.State);
        Assert.Equal(10, report.AnimalsAtRisk.Value.Value);
        Assert.Equal(ReportedValueState.Known, report.AnimalsAffected.Value.State);
        Assert.Equal(3, report.AnimalsAffected.Value.Value);
        Assert.Equal(ReportedValueState.Known, report.AnimalsDead.Value.State);
        Assert.Equal(4, report.AnimalsDead.Value.Value);
        Assert.Equal(reportId, report.ReportId);
    }

    [Fact]
    public void AnimalCountsWithDifferentPopulationsOrWindows_AreNotCompared()
    {
        var report = AnimalReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown),
            AnimalSubjectScope.Herd,
            ReportedFact<string>.Unknown(ObservationSource),
            ReportedFact<string>.Unknown(ObservationSource),
            new ReportEvidence(null, [new ReportedText("Group report.", ObservationSource)], null),
            AnimalCount.Known(2, ObservationSource, "herd:one", Period(new DateOnly(2026, 10, 20))),
            AnimalCount.Known(8, ObservationSource, "herd:two", Period(new DateOnly(2026, 10, 20))),
            AnimalCount.Known(9, ObservationSource, "herd:two", Period(new DateOnly(2026, 10, 21))));

        Assert.Empty(report.Validate());
    }

    [Fact]
    public void AnimalCountsWithUnknownPopulationOrObservationPeriod_AreNotCompared()
    {
        var report = AnimalReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown),
            AnimalSubjectScope.Flock,
            ReportedFact<string>.Unknown(ObservationSource),
            ReportedFact<string>.Unknown(ObservationSource),
            new ReportEvidence(null, [new ReportedText("Animals appeared unwell.", ObservationSource)], null),
            AnimalCount.Known(2, ObservationSource),
            AnimalCount.Known(8, ObservationSource),
            AnimalCount.Unknown(ObservationSource));

        Assert.Empty(report.Validate());
        Assert.Equal(ReportedValueState.Unknown, report.Species.Value.State);
        Assert.Equal(ReportedValueState.Unknown, report.AnimalsDead.Value.State);
    }

    [Fact]
    public void AnimalConcernIsAcceptedWithoutKnownDiseaseOrInferredClassification()
    {
        var report = AnimalReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown),
            AnimalSubjectScope.Individual,
            ReportedFact<string>.Known("cattle", ObservationSource),
            ReportedFact<string>.Unknown(ObservationSource),
            new ReportEvidence(null, [new ReportedText("Acute watery diarrhoea; cause not known.", ObservationSource)], null),
            AnimalCount.Unknown(ObservationSource),
            AnimalCount.Unknown(ObservationSource),
            AnimalCount.Unknown(ObservationSource));

        Assert.Empty(report.Validate());
        Assert.Equal(ReportedValueState.Unknown, report.HoldingReference.Value.State);
        Assert.Null(typeof(AnimalReport).GetProperty("Classification"));
    }

    [Fact]
    public void AnimalReport_RequiresAnExplicitDefinedSubjectScope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimalReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown),
            (AnimalSubjectScope)int.MaxValue,
            ReportedFact<string>.Unknown(ObservationSource),
            ReportedFact<string>.Unknown(ObservationSource),
            new ReportEvidence(null, [new ReportedText("Group report.", ObservationSource)], null),
            AnimalCount.Unknown(ObservationSource),
            AnimalCount.Unknown(ObservationSource),
            AnimalCount.Unknown(ObservationSource)));
    }

    [Fact]
    public void UnknownOrNotRecordedAnimalCountsRemainDistinctFromKnownZero()
    {
        var report = AnimalReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown),
            AnimalSubjectScope.OtherGroup,
            ReportedFact<string>.Unknown(ObservationSource),
            ReportedFact<string>.Unknown(ObservationSource),
            new ReportEvidence(null, [new ReportedText("Uncounted group.", ObservationSource)], null),
            AnimalCount.Unknown(ObservationSource),
            AnimalCount.NotRecorded(ObservationSource),
            AnimalCount.Known(0, ObservationSource, "group:synthetic-1", Period(new DateOnly(2026, 10, 20))));

        Assert.Equal(ReportedValueState.Unknown, report.AnimalsAtRisk.Value.State);
        Assert.Equal(ReportedValueState.NotRecorded, report.AnimalsAffected.Value.State);
        Assert.Equal(ReportedValueState.Known, report.AnimalsDead.Value.State);
        Assert.Equal(0, report.AnimalsDead.Value.Value);
        Assert.Empty(report.Validate());
    }

    [Fact]
    public void ReportEvidenceAndAnimalCounts_AreSnapshottedFromMutableInputLists()
    {
        var observations = new List<ReportedText> { new("Original observation.", ObservationSource) };
        var report = AnimalReport.Create(
            Guid.NewGuid(),
            Context(ReportedValue<ReportedTime>.Unknown),
            AnimalSubjectScope.Flock,
            ReportedFact<string>.Unknown(ObservationSource),
            ReportedFact<string>.Unknown(ObservationSource),
            new ReportEvidence(null, observations, null),
            AnimalCount.Unknown(ObservationSource),
            AnimalCount.Unknown(ObservationSource),
            AnimalCount.Unknown(ObservationSource));

        observations.Add(new ReportedText("Injected after creation.", ObservationSource));

        Assert.Single(report.Evidence.Observations);
        Assert.Equal("Original observation.", report.Evidence.Observations[0].Text);
    }

    private static ReportContext Context(
        ReportedValue<ReportedTime> occurrence,
        LocationContext? location = null,
        ReportedTimePrecision capturePrecision = ReportedTimePrecision.Exact,
        string? capturedOffset = "+02:00",
        string? capturedZoneId = "Africa/Mbabane",
        bool includeCaptureTime = true) => new(
            new ReportedFact<ReportedTime>(occurrence, NarrativeSource),
            includeCaptureTime ? DateTimeOffset.Parse("2026-10-27T10:00:00+02:00") : null,
            capturedOffset,
            capturedZoneId,
            capturePrecision,
            location ?? LocationWithoutCoordinates());

    private static LocationContext LocationWithoutCoordinates(double? latitude = null, double? longitude = null) => new(
        "SZ",
        new OptionalPlace(null, null, PresenceState.Unknown),
        new OptionalPlace(null, null, PresenceState.Unknown),
        new OptionalPlace(null, null, PresenceState.Unknown),
        latitude,
        longitude,
        null,
        LocationSource.Reporter,
        LocationPrecision.Unknown,
        null);

    private static ReportedFact<ReportedTime> Period(DateOnly date) => ReportedFact<ReportedTime>.Known(ReportedTimeFor(ReportedTimePrecision.DateOnly, date), ObservationSource);

    private static ReportedTime ReportedTimeFor(ReportedTimePrecision precision, DateOnly date) => new(
        null,
        date,
        null,
        null,
        null,
        null,
        precision);
}
