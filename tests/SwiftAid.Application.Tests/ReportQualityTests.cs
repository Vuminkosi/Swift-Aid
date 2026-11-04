using SwiftAid.Application.Classification;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests;

public sealed class ReportQualityTests
{
    [Fact]
    public void Typed_quality_preserves_unknown_precision_and_original_repeated_evidence()
    {
        var receipt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var provenance = new ReportProvenance(ReportSourceKind.ReporterObservation, "fixture-observation");
        var missing = new OptionalPlace(null, null, PresenceState.Unknown);
        var location = new LocationContext("ZW", missing, missing, missing, null, null, null, LocationSource.Reporter, LocationPrecision.Approximate, null);
        var context = new ReportContext(ReportedFact<ReportedTime>.Unknown(provenance), receipt.AddHours(1), "+00:00", null, ReportedTimePrecision.Exact, location);
        var evidence = new ReportEvidence(null, [new("No fever reported", provenance), new("No fever reported", provenance)], null);
        var report = HumanReport.Create(Guid.NewGuid(), context, ReportedFact<string>.Unknown(provenance),
            ReportedFact<int>.Unknown(provenance), ReportedFact<string>.Unknown(provenance), evidence);
        var source = new CaseReportEventData.HumanV2(report, new(true, "ZW", "fixture", true, true, "fixture", "1", null, null, null));
        var result = ReportQualityRules.Assess(source, receipt);
        Assert.Equal(new[] { "occurrence-not-known", "capture-after-receipt", "geography-not-exact", "region-not-known", "repeated-observation-text" }, result.Findings.Select(f => f.Code));
        Assert.Null(report.Context.Location.Latitude);
        Assert.False(report.Context.Occurrence.Value.HasValue);
        Assert.Equal(2, report.Evidence.Observations.Count);
        Assert.DoesNotContain(result.Findings, f => f.Explanation.Contains("No fever reported", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_missing_context_is_explicit_without_inventing_a_time_or_location()
    {
        var source = new CaseReportEventData.LegacyV1(new ReportFacts(CaseKind.Human, Sector.HumanHealth, "No fever reported", []));
        var result = ReportQualityRules.Assess(source, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        Assert.Equal("legacy-context-unavailable", Assert.Single(result.Findings).Code);
        Assert.Equal("No fever reported", source.Report.Narrative);
    }
}
