using System.Diagnostics;
using System.Text;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Intake;
using Xunit.Abstractions;

namespace SwiftAid.Integration.Tests;

public sealed class CaseCsvSuppliedFixtureTests(ITestOutputHelper output)
{
    [SuppliedCsvFact]
    public async Task All_supplied_synthetic_rows_parse_and_map_without_loading_whole_files()
    {
        var directory = Environment.GetEnvironmentVariable("SWIFTAID_TEST_CASE_CSV_DIRECTORY")!;
        foreach (var (file, country, expected) in new[] { ("zimbabwe_10500_cases.csv", "ZW", 10500), ("eswatini_4500_cases.csv", "SZ", 4500) })
        {
            var stopwatch = Stopwatch.StartNew();
            var context = new CaseImportContext("workspace-fixture", new IntakeConfirmations(true, country, "catalogue/fixture", true, true, "synthetic-policy", "1", null, null, null));
            using var stream = File.OpenRead(Path.Combine(directory, file));
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            IReadOnlyList<string>? header = null;
            var count = 0;
            await foreach (var row in BoundedCsvReader.ReadAsync(reader))
            {
                if (header is null) { header = row.Fields; CaseCsvImportAdapter.ValidateHeader(header); continue; }
                var parsed = CaseCsvImportAdapter.Parse(row, header, context, "officer-fixture");
                Assert.True(parsed.IsValid, $"{file} row {row.Number}: {parsed.ErrorCode}");
                var record = parsed.Record!;
                _ = IntakePromotionMapper.ToCapturedReport(record.CommandId, $"import:{record.CommandId:N}", record.Submission);
                count++;
            }
            Assert.Equal(expected, count);
            output.WriteLine($"{file}: {count} parsed/mapped rows in {stopwatch.Elapsed.TotalSeconds:F3}s; no persistence or provider calls.");
        }
    }
}

public sealed class SuppliedCsvFactAttribute : FactAttribute
{
    public SuppliedCsvFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_CASE_CSV_DIRECTORY")))
            Skip = "Set SWIFTAID_TEST_CASE_CSV_DIRECTORY to the supplied synthetic fixture directory.";
    }
}
