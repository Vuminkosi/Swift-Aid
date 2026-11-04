using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Intake;

namespace SwiftAid.Application.Intake.Imports;

public sealed record CaseImportContext(string WorkspaceId, IntakeConfirmations Confirmations,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? OrganizationId = null);

/// <summary>Original CSV values remain separate from their normalized submission. Source IDs never confer authority.</summary>
public sealed record CaseImportRecord(long RowNumber, Guid CommandId, Guid? SourceCaseId, string ExternalRecordId,
    string SourceReference, string SourceLanguage, IntakeSubmission Submission, byte[] SourceHash,
    IReadOnlyDictionary<string, string> OriginalFields);

public sealed record CaseImportValidation(CaseImportRecord? Record, string? ErrorCode)
{
    public bool IsValid => Record is not null && ErrorCode is null;
}

/// <summary>Compatibility adapter for the supplied Zimbabwe/Eswatini case CSVs, not a direct database importer.</summary>
public static class CaseCsvImportAdapter
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

    public static CaseImportValidation Parse(CsvRecord row, IReadOnlyList<string> header, CaseImportContext context, string actorId)
    {
        if (row.Fields.Count != header.Count) return new(null, "column-count");
        var fields = header.Zip(row.Fields).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        try
        {
            if (fields["schema_version"] != "1") return new(null, "schema-version");
            if (!Guid.TryParseExact(fields["command_id"], "D", out var commandId) || commandId == Guid.Empty)
                return new(null, "command-id");
            Guid? sourceCaseId = null;
            if (fields["case_id"].Length != 0)
            {
                if (!Guid.TryParseExact(fields["case_id"], "D", out var parsedCaseId) || parsedCaseId == Guid.Empty)
                    return new(null, "source-case-id");
                sourceCaseId = parsedCaseId;
            }
            if (fields["workspace_id"].Length != 0 && fields["workspace_id"] != context.WorkspaceId)
                return new(null, "workspace-mismatch");
            if (fields["accountable_owner_id"].Length != 0 && fields["accountable_owner_id"] != actorId)
                return new(null, "owner-mismatch");
            if (fields["external_record_id"].Length is < 1 or > 200 || fields["source_reference"].Length is < 1 or > 300 ||
                fields["source_language"].Length is < 2 or > 16) return new(null, "source-reference");
            if (!Enum.TryParse<IntakeSubjectScope>(fields["subject_scope"], false, out var scope) || !Enum.IsDefined(scope))
                return new(null, "subject-scope");
            if (!Enum.TryParse<LocationPrecision>(fields["location_precision"], false, out var precision) || !Enum.IsDefined(precision))
                return new(null, "location-precision");
            if (fields["country_code"] != context.Confirmations.OccurrenceCountryCode)
                return new(null, "country-mismatch");
            var observations = ParseObservations(fields["observations_json"]);
            var latitude = Number(fields["latitude"]);
            var longitude = Number(fields["longitude"]);
            var accuracy = fields.TryGetValue("accuracy_metres", out var accuracyText) ? Number(accuracyText) : null;
            // Existing files omit coordinates. If future files supply them, do not invent accuracy.
            if ((latitude is not null || longitude is not null) && accuracy is null) return new(null, "coordinate-accuracy-required");
            var location = new LocationContext(fields["country_code"], Place(fields["region"]), Place(fields["area"]),
                Place(fields["locality"], fields["location_id"]), latitude, longitude, accuracy,
                LocationSource.Reporter, precision, context.Confirmations.OccurrenceCountryCatalogueVersion);
            var submission = new IntakeSubmission(1, commandId, scope,
                fields["narrative"].Length == 0 ? null : fields["narrative"], observations, [],
                ParseTime(fields["captured_at"]), ParseTime(fields["occurred_at"]), location, context.Confirmations);
            IntakeSubmissionRules.Validate(submission, new IntakeLimits());
            var originalBytes = JsonSerializer.SerializeToUtf8Bytes(fields.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            var hash = SHA256.HashData(originalBytes);
            CryptographicOperations.ZeroMemory(originalBytes);
            return new(new CaseImportRecord(row.Number - 1, commandId, sourceCaseId, fields["external_record_id"],
                fields["source_reference"], fields["source_language"], submission, hash, fields), null);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        {
            return new(null, "invalid-report");
        }
    }

    public static IReadOnlyList<string> ParseObservations(string json)
    {
        if (json.Length > BoundedCsvReader.MaximumFieldCharacters) throw new ArgumentException("observations-too-large");
        var observations = JsonSerializer.Deserialize<string[]>(json, new JsonSerializerOptions { MaxDepth = 2 })
            ?? throw new ArgumentException("observations-must-be-string-array");
        if (observations.Length > 32 || observations.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 300))
            throw new ArgumentException("invalid-observations");
        return observations;
    }

    private static OptionalPlace Place(string name, string? id = null) =>
        new(name.Length == 0 ? null : name, string.IsNullOrEmpty(id) ? null : id,
            name.Length == 0 && string.IsNullOrEmpty(id) ? PresenceState.Unknown : PresenceState.Known);

    private static double? Number(string text) => text.Length == 0 ? null : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static ReportedTime ParseTime(string text)
    {
        if (text.Length == 0) return new(null, null, null, null, null, null, ReportedTimePrecision.Unknown);
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return new(null, date, null, null, null, null, ReportedTimePrecision.DateOnly);
        // Require an explicit source offset. Never interpret a file using the server's local time zone.
        if (!(text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':'))
            throw new FormatException("timestamp-offset-required");
        var value = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return new(value.ToUniversalTime(), null, null, null,
            value.ToString("zzz", CultureInfo.InvariantCulture), null, ReportedTimePrecision.Exact);
    }
}
