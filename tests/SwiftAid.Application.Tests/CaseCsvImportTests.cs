using System.Text.Json;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests;

public sealed class CaseCsvImportTests
{
    [Fact]
    public async Task Quoted_observations_and_multiline_text_survive_buffer_boundaries()
    {
        var records = new List<CsvRecord>();
        await foreach (var row in BoundedCsvReader.ReadAsync(new SmallReads("a,b\r\n\"line1\nline2\",\"[\"\"reported cough\"\",\"\"reported fever\"\"]\"\r\n")))
            records.Add(row);
        Assert.Equal(2, records.Count);
        Assert.Equal("line1\nline2", records[1].Fields[0]);
        Assert.Equal(new[] { "reported cough", "reported fever" }, CaseCsvImportAdapter.ParseObservations(records[1].Fields[1]));
    }

    [Theory]
    [InlineData("\"unclosed")]
    [InlineData("a\"b,c")]
    [InlineData("\"closed\"tail,c")]
    public async Task Malformed_csv_fails_without_inventing_records(string text)
    {
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var _ in BoundedCsvReader.ReadAsync(new StringReader(text))) { } });
    }

    [Fact]
    public async Task Oversized_field_is_rejected_while_reading()
    {
        var reader = new StringReader(new string('x', BoundedCsvReader.MaximumFieldCharacters + 1));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var _ in BoundedCsvReader.ReadAsync(reader)) { } });
    }

    [Theory]
    [InlineData("{\"observation\":\"cough\"}")]
    [InlineData("[{}]")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    public void Observations_remain_a_bounded_array_of_strings(string text) =>
        Assert.ThrowsAny<Exception>(() => CaseCsvImportAdapter.ParseObservations(text));

    [Fact]
    public void Header_rejects_duplicate_unknown_and_missing_columns()
    {
        Assert.Throws<InvalidDataException>(() => CaseCsvImportAdapter.ValidateHeader([.. CaseCsvImportAdapter.Columns, "command_id"]));
        Assert.Throws<InvalidDataException>(() => CaseCsvImportAdapter.ValidateHeader([.. CaseCsvImportAdapter.Columns, "diagnosis"]));
        Assert.Throws<InvalidDataException>(() => CaseCsvImportAdapter.ValidateHeader(CaseCsvImportAdapter.Columns.Skip(1).ToArray()));
    }

    [Fact]
    public void Import_maps_source_facts_without_promoting_a_receipt_or_assigning_supplied_owners()
    {
        var (row, context) = Example();
        var parsed = CaseCsvImportAdapter.Parse(row, CaseCsvImportAdapter.Columns, context, "officer-1");
        Assert.True(parsed.IsValid, parsed.ErrorCode);
        var record = parsed.Record!;
        var report = Assert.IsType<CaseReportEventData.HumanV2>(IntakePromotionMapper.ToCapturedReport(
            record.CommandId, $"import:{record.CommandId:N}", record.Submission));
        Assert.Equal("A reported concern.", report.Report.Evidence.Narrative!.Text);
        Assert.Equal("Cough reported", Assert.Single(report.Report.Evidence.Observations).Text);
        Assert.StartsWith("import:", report.Report.Evidence.Narrative.Provenance.StableReference);
        Assert.False(report.Report.AgeYears.Value.HasValue);
        Assert.Equal(TimeSpan.Zero, record.Submission.CapturedAt.Value!.Value.Offset);
        Assert.Equal("+02:00", record.Submission.CapturedAt.OriginalOffset);
        var changed = row.Fields.ToArray();
        changed[17] = "someone-else";
        Assert.Equal("owner-mismatch", CaseCsvImportAdapter.Parse(row with { Fields = changed }, CaseCsvImportAdapter.Columns, context, "officer-1").ErrorCode);
    }

    [Fact]
    public void Same_row_has_stable_identity_and_source_hash_but_changed_facts_do_not()
    {
        var (row, context) = Example();
        var a = CaseCsvImportAdapter.Parse(row, CaseCsvImportAdapter.Columns, context, "officer-1").Record!;
        var b = CaseCsvImportAdapter.Parse(row, CaseCsvImportAdapter.Columns, context, "officer-1").Record!;
        Assert.Equal(a.CommandId, b.CommandId);
        Assert.Equal(a.SourceHash, b.SourceHash);
        var fields = row.Fields.ToArray();
        fields[5] = "Changed supplied narrative.";
        var c = CaseCsvImportAdapter.Parse(row with { Fields = fields }, CaseCsvImportAdapter.Columns, context, "officer-1").Record!;
        Assert.NotEqual(Convert.ToHexString(a.SourceHash), Convert.ToHexString(c.SourceHash));
    }

    [Fact]
    public async Task Cancellation_stops_reader()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in BoundedCsvReader.ReadAsync(new StringReader("a,b"), cancelled.Token)) { } });
    }

    internal static (CsvRecord Row, CaseImportContext Context) Example()
    {
        var context = new CaseImportContext("workspace-1", new IntakeConfirmations(true, "ZW", "catalogue/1", true, true, "policy", "1", null, null, null));
        return (new CsvRecord(2, ["1", "bdd5e5f2-c76a-5da8-8991-e0e13b0218ef", "a8d50d35-fdbd-5d48-99e1-e575d11c5e68", "SYNTHETIC-1", "Human",
            "A reported concern.", "[\"Cough reported\"]", "2026-06-04T16:46:59+02:00", "2026-06-02T21:51:59+02:00", "ZW", "Manicaland", "", "Rusape", "ZW:place:rusape", "", "", "Approximate", "", "", "sn", "SYNTHETIC:SWIFTAID:1"]), context);
    }

    private sealed class SmallReads(string text) : StringReader(text)
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
}
