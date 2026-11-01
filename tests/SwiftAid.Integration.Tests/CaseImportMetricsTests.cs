using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Xunit.Abstractions;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class CaseImportMetricsTests(ITestOutputHelper output)
{
    [IntakePostgresFact]
    public async Task Real_import_emits_bounded_stage_latency_and_committed_outcomes_without_source_identifiers()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var durations = new ConcurrentBag<(string Stage, double Milliseconds)>();
        var outcomes = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "SwiftAid.Ingestion.Imports") current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            Assert.Equal("swiftaid.ingestion.import.duration", instrument.Name);
            Assert.Equal("ms", instrument.Unit);
            var tag = Assert.Single(tags.ToArray());
            Assert.Equal("stage", tag.Key);
            durations.Add(((string)tag.Value!, value));
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            Assert.Equal("swiftaid.ingestion.import.rows", instrument.Name);
            Assert.Equal(1, value);
            var tag = Assert.Single(tags.ToArray());
            Assert.Equal("status", tag.Key);
            outcomes.Add((string)tag.Value!);
        });
        listener.Start();
        var importId = Guid.NewGuid();
        var csv = CaseImportPostgresTests.Csv(Guid.NewGuid());
        var accepted = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, importId, CaseImportPostgresTests.Context, 1, true, new StringReader(csv), default);
        Assert.Equal("Accepted", Assert.Single(accepted.Rows).Status);
        var replay = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, importId, CaseImportPostgresTests.Context, 1, true, new StringReader(csv), default);
        Assert.Equal("Replayed", Assert.Single(replay.Rows).Status);
        Assert.Equal(new[] { "Accepted", "Replayed" }, outcomes.Order().ToArray());
        Assert.Equal(10, durations.Count);
        foreach (var stage in new[] { "batch", "admission", "validation", "registration", "checkpoint" })
        {
            var samples = durations.Where(item => item.Stage == stage).Select(item => item.Milliseconds).ToArray();
            Assert.Equal(2, samples.Length);
            Assert.All(samples, value => Assert.True(double.IsFinite(value) && value >= 0));
            output.WriteLine($"{stage}: {string.Join(", ", samples.Select(value => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))} ms (two functional samples, not a capacity benchmark)");
        }
    }
}
