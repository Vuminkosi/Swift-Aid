using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Infrastructure.Demo;

/// <summary>One validated demo dataset row.</summary>
public sealed record DemoCaseRow(
    string CountryCode,
    string? RegionId,
    Sector Sector,
    CaseKind Kind,
    CaseWorkflow Workflow,
    int CreatedDaysAgo,
    string SensitivityCode);

/// <summary>Strict CSV parser for the development demo dataset. Unknown or malformed rows are reported, never guessed.</summary>
public static class DemoDataCsvParser
{
    private const int ExpectedColumns = 7;

    public static (IReadOnlyList<DemoCaseRow> Rows, IReadOnlyList<string> Errors) Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var rows = new List<DemoCaseRow>();
        var errors = new List<string>();
        var lines = csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            errors.Add("The demo dataset is empty.");
            return (rows, errors);
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index].Trim();
            if (index == 0 && IsHeader(line))
            {
                continue;
            }

            var columns = line.Split(',');
            if (columns.Length != ExpectedColumns)
            {
                errors.Add($"Line {lineNumber}: expected {ExpectedColumns} columns, found {columns.Length}.");
                continue;
            }

            var row = TryParseRow(columns, lineNumber);
            if (row is null)
            {
                errors.Add($"Line {lineNumber}: invalid demo record.");
                continue;
            }

            rows.Add(row);
        }

        return (rows, errors);
    }

    private static bool IsHeader(string line) =>
        line.StartsWith("country_code,", StringComparison.OrdinalIgnoreCase);

    private static DemoCaseRow? TryParseRow(string[] columns, int lineNumber)
    {
        var country = columns[0].Trim();
        var region = columns[1].Trim();
        var sectorText = columns[2].Trim();
        var kindText = columns[3].Trim();
        var workflowText = columns[4].Trim();
        var daysText = columns[5].Trim();
        var sensitivity = columns[6].Trim();

        if (!IsCode(country) ||
            (region.Length > 0 && !IsCode(region)) ||
            !Enum.TryParse<Sector>(sectorText, ignoreCase: false, out var sector) ||
            !Enum.IsDefined(sector) ||
            !Enum.TryParse<CaseKind>(kindText, ignoreCase: false, out var kind) ||
            !Enum.IsDefined(kind) ||
            !Enum.TryParse<CaseWorkflow>(workflowText, ignoreCase: false, out var workflow) ||
            !Enum.IsDefined(workflow) ||
            !int.TryParse(daysText, out var days) ||
            days is < 0 or > 365 ||
            string.IsNullOrWhiteSpace(sensitivity) ||
            sensitivity.Length > 24)
        {
            return null;
        }

        // The managed_cases check constraint requires kind and sector to agree.
        var pairValid = (kind == CaseKind.Human && sector == Sector.HumanHealth) ||
            (kind == CaseKind.Animal && sector == Sector.AnimalHealth);
        if (!pairValid)
        {
            return null;
        }

        return new DemoCaseRow(
            country,
            region.Length == 0 ? null : region,
            sector,
            kind,
            workflow,
            days,
            sensitivity);
    }

    private static bool IsCode(string value) =>
        value.Length is >= 2 and <= 24 && value.All(IsCodeCharacter);

    private static bool IsCodeCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value == '-';
}
