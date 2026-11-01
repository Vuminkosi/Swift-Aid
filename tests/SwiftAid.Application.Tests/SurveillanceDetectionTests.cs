using SwiftAid.Application.Surveillance;

namespace SwiftAid.Application.Tests;

public sealed class SurveillanceDetectionTests
{
    private static readonly DateTimeOffset End = new(2026, 6, 5, 0, 0, 0, TimeSpan.Zero);

    private static SurveillanceObservation Report(int day, string source, bool respiratory = true) => new(
        Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), source, End.AddDays(day),
        ["country:ZW", "region:ZW/test"], respiratory ? ["respiratory"] : [], []);

    [Fact]
    public void Independent_reports_form_a_candidate_but_not_an_incidence_claim()
    {
        var result = SurveillanceDetector.Assess("region:ZW/test", "respiratory", End,
            [Report(-1, "a"), Report(-2, "b"), Report(-3, "c")]);
        Assert.True(result.Candidate);
        Assert.Equal(3, result.Observed);
        Assert.Equal(3, result.IndependentSources);
        Assert.Null(result.Expected);
        Assert.Contains("insufficient-baseline", result.Limitations);
        Assert.Equal("Unknown", result.Severity);
    }

    [Fact]
    public void Duplicate_cases_and_single_reporting_actor_do_not_create_independent_evidence()
    {
        var one = Report(-1, "a");
        Assert.False(SurveillanceDetector.Assess("region:ZW/test", "respiratory", End,
            [one, one, Report(-2, "a"), Report(-3, "a")]).Candidate);
    }

    [Fact]
    public void Unknown_occurrence_is_not_replaced_by_receipt_time_and_area_is_scoped()
    {
        var unknown = Report(-1, "a") with { OccurredAtUtc = null };
        var elsewhere = Report(-2, "b") with { Areas = ["region:SZ/other"] };
        var result = SurveillanceDetector.Assess("region:ZW/test", "respiratory", End, [unknown, elsewhere]);
        Assert.Equal(0, result.Observed);
        Assert.False(result.Candidate);
    }

    [Fact]
    public void Baseline_adjusts_for_reporting_volume_and_order_does_not_change_evidence_hash()
    {
        var reports = Enumerable.Range(0, 28).Select(i => Report(-4 - i, "base" + i, i % 2 == 0))
            .Concat(Enumerable.Range(0, 10).Select(i => Report(-1, "recent" + i, i % 2 == 0))).ToArray();
        var result = SurveillanceDetector.Assess("region:ZW/test", "respiratory", End, reports);
        Assert.Equal(5d, result.Expected);
        Assert.Equal("NoIncreaseInReportedShare", result.Comparison);
        Assert.Equal(result.EvidenceHash, SurveillanceDetector.Assess("region:ZW/test", "respiratory", End, reports.Reverse().ToArray()).EvidenceHash);
    }
}
