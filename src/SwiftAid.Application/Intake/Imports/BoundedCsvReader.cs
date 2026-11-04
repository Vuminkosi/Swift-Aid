using System.Runtime.CompilerServices;
using System.Text;

namespace SwiftAid.Application.Intake.Imports;

public sealed record CsvRecord(long Number, IReadOnlyList<string> Fields);

/// <summary>RFC 4180-style sequential reader. Bounds apply while reading, before a row can be materialized.</summary>
public static class BoundedCsvReader
{
    public const int MaximumRecordCharacters = 65_536;
    public const int MaximumFieldCharacters = 16_384;
    public const int MaximumColumns = 32;

    public static async IAsyncEnumerable<CsvRecord> ReadAsync(
        TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var buffer = new char[4096];
        var field = new StringBuilder();
        var fields = new List<string>();
        var state = 0; // 0 unquoted, 1 quoted, 2 closing quote
        var characters = 0;
        var skipLineFeed = false;
        long number = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var character = buffer[index];
                if (number == 0 && characters == 0 && fields.Count == 0 && field.Length == 0 && character == '\uFEFF')
                    continue;
                if (skipLineFeed)
                {
                    skipLineFeed = false;
                    if (character == '\n') continue;
                }
                if (++characters > MaximumRecordCharacters) throw new InvalidDataException("csv-record-too-large");
                if (character == '\0') throw new InvalidDataException("csv-invalid-character");
                if (state == 1)
                {
                    if (character == '"') state = 2;
                    else Append(character);
                    continue;
                }
                if (state == 2 && character == '"')
                {
                    Append('"');
                    state = 1;
                    continue;
                }
                if (character == ',')
                {
                    AddField();
                    state = 0;
                    continue;
                }
                if (character is '\r' or '\n')
                {
                    AddField();
                    yield return new CsvRecord(++number, fields.ToArray());
                    fields.Clear();
                    characters = 0;
                    state = 0;
                    skipLineFeed = character == '\r';
                    continue;
                }
                if (state == 2) throw new InvalidDataException("csv-trailing-quoted-content");
                if (character == '"')
                {
                    if (field.Length != 0) throw new InvalidDataException("csv-unexpected-quote");
                    state = 1;
                }
                else Append(character);
            }
        }
        if (state == 1) throw new InvalidDataException("csv-unclosed-quote");
        if (characters > 0)
        {
            AddField();
            yield return new CsvRecord(++number, fields.ToArray());
        }

        void Append(char character)
        {
            if (field.Length >= MaximumFieldCharacters) throw new InvalidDataException("csv-field-too-large");
            field.Append(character);
        }

        void AddField()
        {
            if (fields.Count >= MaximumColumns) throw new InvalidDataException("csv-too-many-columns");
            fields.Add(field.ToString());
            field.Clear();
        }
    }
}
