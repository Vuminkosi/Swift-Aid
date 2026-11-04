using System.Text;
using SwiftAid.Application.Intake.Imports;

namespace SwiftAid.Application.Tests;

public sealed class CaseCsvBatchReaderTests
{
    private static readonly string Header = string.Join(',', CaseCsvImportAdapter.Columns) + "\n";

    [Fact]
    public async Task Bom_multiline_values_batch_boundaries_and_resume_preserve_order()
    {
        var input = "\ufeff" + Header + string.Concat(Enumerable.Range(1, 205).Select(index => $"{index},\"original\ntext\"\n"));
        var batches = await ReadAsync(input);
        Assert.Equal(new[] { 100, 100, 5 }, batches.Select(batch => batch.RowCount));
        Assert.Equal(new long[] { 1, 101, 201 }, batches.Select(batch => batch.StartRow));
        Assert.Equal(new[] { false, false, true }, batches.Select(batch => batch.IsFinal));
        var resumed = await ReadAsync(input, 101);
        Assert.Equal(batches.Skip(1), resumed);
        var values = new List<string>();
        foreach (var batch in batches)
        {
            var records = new List<CsvRecord>();
            await foreach (var record in BoundedCsvReader.ReadAsync(new StringReader(batch.Csv))) records.Add(record);
            values.AddRange(records.Skip(1).Select(record => record.Fields[0]));
            Assert.All(records.Skip(1), record => Assert.Equal("original\ntext", record.Fields[1]));
        }
        Assert.Equal(Enumerable.Range(1, 205).Select(index => index.ToString()), values);
    }

    [Fact]
    public async Task Utf8_byte_bound_is_enforced_independently_of_row_count()
    {
        var input = Header + string.Concat(Enumerable.Repeat(new string('界', 16000) + "\n", 100));
        var batches = await ReadAsync(input);
        Assert.Equal(100, batches.Sum(batch => batch.RowCount));
        Assert.True(batches.Count > 1);
        Assert.All(batches, batch => Assert.InRange(Encoding.UTF8.GetByteCount(batch.Csv), 1, CaseCsvBatchReader.MaximumBatchBytes));
        Assert.True(batches[^1].IsFinal);
    }

    [Fact]
    public async Task End_of_file_resume_emits_only_final_header_but_a_gap_fails()
    {
        var batch = Assert.Single(await ReadAsync(Header + "one\n", 2));
        Assert.Equal(0, batch.RowCount);
        Assert.True(batch.IsFinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(Header + "one\n", 3));
    }

    [Fact]
    public async Task Cancellation_stops_reading()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in CaseCsvBatchReader.ReadAsync(new StringReader(Header), ct: cancellation.Token)) { } });
    }

    private static async Task<List<CaseCsvBatch>> ReadAsync(string input, long start = 1)
    {
        var batches = new List<CaseCsvBatch>();
        await foreach (var batch in CaseCsvBatchReader.ReadAsync(new StringReader(input), start)) batches.Add(batch);
        return batches;
    }
}
