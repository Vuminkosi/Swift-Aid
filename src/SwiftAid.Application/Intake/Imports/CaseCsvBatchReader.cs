using System.Runtime.CompilerServices;

namespace SwiftAid.Application.Intake.Imports;

public sealed record CaseCsvBatch(long StartRow, int RowCount, bool IsFinal, string Csv);

/// <summary>Application facade over the shared bounded batch reader.</summary>
public static class CaseCsvBatchReader
{
    public const int MaximumBatchBytes = SwiftAid.Contracts.Intake.Imports.CaseCsvBatchReader.MaximumBatchBytes;
    public const int MaximumBatchRows = SwiftAid.Contracts.Intake.Imports.CaseCsvBatchReader.MaximumBatchRows;

    public static async IAsyncEnumerable<CaseCsvBatch> ReadAsync(TextReader reader, long nextRow = 1,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var batch in SwiftAid.Contracts.Intake.Imports.CaseCsvBatchReader.ReadAsync(reader, nextRow, ct).ConfigureAwait(false))
            yield return new CaseCsvBatch(batch.StartRow, batch.RowCount, batch.IsFinal, batch.Csv);
    }
}
