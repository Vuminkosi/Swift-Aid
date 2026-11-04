using System.Runtime.CompilerServices;

namespace SwiftAid.Application.Intake.Imports;

/// <summary>Compatibility record for application import consumers.</summary>
public sealed record CsvRecord(long Number, IReadOnlyList<string> Fields);

/// <summary>Application facade over the shared bounded CSV reader.</summary>
public static class BoundedCsvReader
{
    public const int MaximumRecordCharacters = SwiftAid.Contracts.Intake.Imports.BoundedCsvReader.MaximumRecordCharacters;
    public const int MaximumFieldCharacters = SwiftAid.Contracts.Intake.Imports.BoundedCsvReader.MaximumFieldCharacters;
    public const int MaximumColumns = SwiftAid.Contracts.Intake.Imports.BoundedCsvReader.MaximumColumns;

    public static async IAsyncEnumerable<CsvRecord> ReadAsync(TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var record in SwiftAid.Contracts.Intake.Imports.BoundedCsvReader.ReadAsync(reader, cancellationToken).ConfigureAwait(false))
            yield return new CsvRecord(record.Number, record.Fields);
    }
}
