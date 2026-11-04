using System.Runtime.CompilerServices;
using System.Text;

namespace SwiftAid.Contracts.Intake.Imports;

public sealed record CaseCsvBatch(long StartRow, int RowCount, bool IsFinal, string Csv);

/// <summary>Streams a selected file into bounded batches without retaining a whole import in client/server memory.</summary>
public static class CaseCsvBatchReader
{
    public const int MaximumBatchBytes = 1024 * 1024;
    public const int MaximumBatchRows = 100;

    public static async IAsyncEnumerable<CaseCsvBatch> ReadAsync(TextReader reader, long nextRow = 1,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (nextRow < 1) throw new ArgumentOutOfRangeException(nameof(nextRow));
        await using var records = BoundedCsvReader.ReadAsync(reader, ct).GetAsyncEnumerator(ct);
        if (!await records.MoveNextAsync()) throw new InvalidDataException("csv-header-missing");
        CaseImportCsvSchema.ValidateHeader(records.Current.Fields);
        var header = Serialize(records.Current.Fields);
        for (long skipped = 1; skipped < nextRow; skipped++)
            if (!await records.MoveNextAsync()) throw new InvalidDataException("checkpoint-past-file");

        var hasNext = await records.MoveNextAsync();
        var pending = hasNext ? Serialize(records.Current.Fields) : null;
        do
        {
            var batch = new StringBuilder(header);
            var bytes = Encoding.UTF8.GetByteCount(header);
            var count = 0;
            while (pending is not null && count < MaximumBatchRows)
            {
                var recordBytes = Encoding.UTF8.GetByteCount(pending);
                if (bytes + recordBytes > MaximumBatchBytes) break;
                batch.Append(pending);
                bytes += recordBytes;
                count++;
                hasNext = await records.MoveNextAsync();
                pending = hasNext ? Serialize(records.Current.Fields) : null;
            }
            if (count == 0 && pending is not null) throw new InvalidDataException("row-too-large");
            yield return new(nextRow, count, pending is null, batch.ToString());
            nextRow += count;
        } while (pending is not null);
    }

    private static string Serialize(IReadOnlyList<string> fields) =>
        string.Join(',', fields.Select(value => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")) + "\n";
}
