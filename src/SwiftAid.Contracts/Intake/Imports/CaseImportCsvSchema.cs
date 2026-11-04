namespace SwiftAid.Contracts.Intake.Imports;

/// <summary>Shared CSV header rules for bounded case import batches.</summary>
public static class CaseImportCsvSchema
{
    public static readonly IReadOnlyList<string> Columns = Array.AsReadOnly(new[]
    {
        "schema_version", "command_id", "case_id", "external_record_id", "subject_scope", "narrative",
        "observations_json", "captured_at", "occurred_at", "country_code", "region", "area", "locality",
        "location_id", "latitude", "longitude", "location_precision", "accountable_owner_id", "workspace_id",
        "source_language", "source_reference"
    });

    public static void ValidateHeader(IReadOnlyList<string> header)
    {
        if (header.Count != header.Distinct(StringComparer.Ordinal).Count() ||
            Columns.Any(column => !header.Contains(column, StringComparer.Ordinal)) ||
            header.Any(column => !Columns.Contains(column, StringComparer.Ordinal) && column != "accuracy_metres"))
            throw new InvalidDataException("csv-header-invalid");
    }
}
