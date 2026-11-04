using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwiftAid.Application.Ellai;

/// <summary>Canonical, versioned argument contracts for model-visible reads.
/// Values are strings on the model-turn wire; handlers enforce domain-specific
/// formats and authorization after this structural validation.</summary>
public static class EllaiToolContractCatalog
{
    public const int CurrentSchemaVersion = 6;

    private const int ReferenceLength = 120;
    private const int ObservationJsonLength = 16_384;

    private static readonly IReadOnlyDictionary<string, string> ArgumentDescriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projection"] = "Requested state projection.",
            ["limit"] = "Maximum number of returned items, subject to the tool's narrower limit.",
            ["ref"] = "Exact bounded reference or topic text.",
            ["country"] = "Uppercase ISO 3166-1 alpha-2 country code.",
            ["place_id"] = "Exact selected occurrence-place identifier from the geographic registry; provide the matching country.",
            ["workflow"] = "Recorded case workflow filter.",
            ["sort"] = "Supported queue ordering.",
            ["group_by"] = "Optional authorized case grouping dimension; for retained evidence, groups only the exact earlier result set. Evidence also supports reported-day in UTC.",
            ["reported_from"] = "Inclusive UTC calendar date for managed-case creation/report time; use with reported_before.",
            ["reported_before"] = "Exclusive UTC calendar date for managed-case creation/report time; use with reported_from.",
            ["observed_term"] = "Optional literal substring to match case-insensitively in currently authorized verbatim reported observations; it is not a diagnosis or clinical classifier.",
            ["field"] = "Optional country, workflow, verbatim reported observations or recorded submitter attribution for retained cases.",
            ["classification_filter"] = "Filter retained, currently authorized case results by recorded classified or unclassified status; does not return classifier labels or scores.",
            ["feature"] = "Feature identifier in the current authorized scope.",
            ["receipt"] = "Receipt identifier bound to the current session.",
            ["topic"] = "Optional approved product fact topic.",
            ["q"] = "Non-empty bounded search query.",
            ["section"] = "Exact approved technical-document section identifier.",
            ["goal"] = "Optional UI goal tag used only as a discovery hint.",
            ["controls"] = "Comma-separated registered UI control identifiers.",
            ["view"] = "Current view instance identifier or current.",
            ["context"] = "Comma-separated UI context tags.",
            ["observations_json"] = "JSON array of original reported observation strings.",
            ["workspace_id"] = "Authorized import workspace identifier.",
            ["organization_id"] = "Authorized destination organization identifier.",
            ["import_id"] = "Canonical import identifier.",
            ["case_id"] = "Canonical case identifier.",
            ["window_days"] = "Required whole-day lookback window from 1 to 365 days.",
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedValues =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["field"] = Array.AsReadOnly(new[] { "country", "workflow", "observations", "submitter" }),
            ["group_by"] = Array.AsReadOnly(new[] { "region", "locality" })
        };

    private static readonly IReadOnlyDictionary<string, ToolContract> Contracts =
        new ReadOnlyDictionary<string, ToolContract>(new Dictionary<string, ToolContract>(StringComparer.Ordinal)
        {
            [EllaiToolNames.State] = Contract(EllaiToolNames.State,
                ("projection", false), ("limit", false),
                ("country", false), ("workflow", false), ("sort", false), ("place_id", false),
                ("reported_from", false), ("reported_before", false), ("group_by", false), ("observed_term", false)),
            [EllaiToolNames.Evidence] = Contract(EllaiToolNames.Evidence, ("ref", true), ("field", false), ("classification_filter", false), ("group_by", false), ("country", false), ("place_id", false), ("case_id", false)),
            [EllaiToolNames.Topic] = Contract(EllaiToolNames.Topic, ("ref", true)),
            [EllaiToolNames.Capabilities] = Contract(EllaiToolNames.Capabilities, ("feature", false)),
            [EllaiToolNames.Receipts] = Contract(EllaiToolNames.Receipts, ("receipt", false)),
            [EllaiToolNames.ProductFacts] = Contract(EllaiToolNames.ProductFacts, ("topic", false)),
            [EllaiToolNames.Now] = Contract(EllaiToolNames.Now),
            [EllaiToolNames.DocsSearch] = Contract(EllaiToolNames.DocsSearch, ("q", true), ("limit", false)),
            [EllaiToolNames.DocsGet] = Contract(EllaiToolNames.DocsGet, ("section", true)),
            [EllaiToolNames.UiCatalogue] = Contract(EllaiToolNames.UiCatalogue, ("feature", false), ("goal", false), ("limit", false)),
            [EllaiToolNames.UiObserve] = Contract(EllaiToolNames.UiObserve, ("controls", true), ("view", true), ("context", false), ("feature", false)),
            [EllaiToolNames.IngestionSchema] = Contract(EllaiToolNames.IngestionSchema),
            [EllaiToolNames.ValidateObservations] = Contract(EllaiToolNames.ValidateObservations, ("observations_json", true)),
            [EllaiToolNames.Locations] = Contract(EllaiToolNames.Locations, ("q", true), ("country", false), ("limit", false)),
            [EllaiToolNames.ImportDestinations] = Contract(EllaiToolNames.ImportDestinations),
            [EllaiToolNames.Imports] = Contract(EllaiToolNames.Imports, ("workspace_id", true), ("organization_id", true), ("country", true)),
            [EllaiToolNames.ImportStatus] = Contract(EllaiToolNames.ImportStatus, ("import_id", true)),
            [EllaiToolNames.ClassificationEvidence] = Contract(EllaiToolNames.ClassificationEvidence, ("case_id", true)),
            [EllaiToolNames.OwnedCaseCount] = Contract(EllaiToolNames.OwnedCaseCount, ("window_days", true)),
        });

    /// <summary>Read-only contracts indexed by canonical advertised tool name.</summary>
    public static IReadOnlyDictionary<string, ToolContract> All => Contracts;

    public static bool TryGet(string toolName, out ToolContract contract) =>
        Contracts.TryGetValue(toolName, out contract!);

    /// <summary>Checks required and unknown properties and the common wire
    /// value bounds. Domain-specific enum, identifier and numeric checks stay
    /// in the tool handler.</summary>
    public static bool TryValidateArguments(
        string toolName, IReadOnlyDictionary<string, string> arguments, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryGet(toolName, out var contract))
        {
            error = "Tool has no registered argument contract.";
            return false;
        }

        var unknown = arguments.Keys.FirstOrDefault(key => !contract.Arguments.ContainsKey(key));
        if (unknown is not null)
        {
            error = $"Argument '{unknown}' is not declared for this tool.";
            return false;
        }

        var missing = contract.Arguments.Values.FirstOrDefault(argument =>
            argument.Required && !arguments.ContainsKey(argument.Name));
        if (missing is not null)
        {
            error = $"Required argument '{missing.Name}' is missing.";
            return false;
        }

        foreach (var (name, value) in arguments)
        {
            var argument = contract.Arguments[name];
            var limit = argument.MaxLength;
            if (value is null || value.Length > limit)
            {
                error = $"Argument '{name}' exceeds its {limit}-character string bound.";
                return false;
            }

            if (value.Length < argument.MinimumLength
                || argument.Pattern is not null && !Regex.IsMatch(value, argument.Pattern, RegexOptions.CultureInvariant))
            {
                error = $"Argument '{name}' does not match its declared string format.";
                return false;
            }

            if (argument.AllowedValues is { Count: > 0 } values && !values.Contains(value, StringComparer.Ordinal))
            {
                error = $"Argument '{name}' has an unsupported value.";
                return false;
            }
            if (toolName == EllaiToolNames.Evidence && name == "ref" &&
                value is not (EllaiCaseResultMemory.Reference or EllaiCaseResultMemory.OriginalReference))
            {
                error = "Argument 'ref' is not a supported evidence reference.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>Produces the JSON Schema 2020-12 object schema used to expose
    /// a tool's exact structural arguments to clients and contract tests.</summary>
    public static string ToJsonSchema(string toolName)
    {
        if (!TryGet(toolName, out var contract))
            throw new ArgumentException("Tool has no registered argument contract.", nameof(toolName));

        var properties = contract.Arguments.Values.ToDictionary(
            argument => argument.Name,
            argument =>
            {
                var property = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "string",
                    ["minLength"] = argument.MinimumLength,
                    ["maxLength"] = argument.MaxLength,
                    ["description"] = argument.Description
                };
                if (argument.Pattern is not null)
                    property["pattern"] = argument.Pattern;
                if (argument.AllowedValues is { Count: > 0 })
                    property["enum"] = argument.AllowedValues;
                else if (toolName == EllaiToolNames.Evidence && argument.Name == "ref")
                    property["enum"] = new[] { EllaiCaseResultMemory.Reference, EllaiCaseResultMemory.OriginalReference };
                return property;
            },
            StringComparer.Ordinal);
        var schema = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = $"https://swiftaid.invalid/contracts/ellai/tools/{toolName}/v{CurrentSchemaVersion}",
            ["x-swiftaid-schema-version"] = CurrentSchemaVersion,
            ["title"] = $"{toolName} arguments",
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = contract.Arguments.Values.Where(argument => argument.Required).Select(argument => argument.Name).ToArray(),
            ["additionalProperties"] = false
        };
        return JsonSerializer.Serialize(schema);
    }

    /// <summary>Shared JSON Schema 2020-12 contract for the serialized
    /// EllaiToolResult envelope and its evidence references.</summary>
    public static string ToResultJsonSchema()
    {
        var evidenceProperties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sourceKind"] = new { type = "string" },
            ["resourceId"] = new { type = "string" },
            ["version"] = new { type = "string" },
            ["fieldPath"] = new { type = new[] { "string", "null" } },
            ["evidenceId"] = new { type = "string" }
        };
        var evidenceSchema = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = evidenceProperties,
            ["required"] = new[] { "sourceKind", "resourceId", "version", "fieldPath", "evidenceId" },
            ["additionalProperties"] = false
        };
        var schema = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = $"https://swiftaid.invalid/contracts/ellai/tool-result/v{CurrentSchemaVersion}",
            ["x-swiftaid-schema-version"] = CurrentSchemaVersion,
            ["title"] = "EllaiToolResult",
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["toolName"] = new { type = "string" },
                ["allowed"] = new { type = "boolean" },
                ["denialReason"] = new { type = new[] { "string", "null" } },
                ["evidence"] = new { type = "array", items = evidenceSchema, maxItems = EllaiToolBroker.MaxItems },
                ["resultSummary"] = new { type = "string" },
                ["itemCount"] = new { type = "integer", minimum = 0, maximum = EllaiToolBroker.MaxItems },
                ["truncated"] = new { type = "boolean" },
                ["reasonCode"] = new
                {
                    type = "string",
                    @enum = new[]
                    {
                        EllaiToolReasonCodes.Ok, EllaiToolReasonCodes.UnregisteredTool,
                        EllaiToolReasonCodes.BudgetExceeded, EllaiToolReasonCodes.MissingScope,
                        EllaiToolReasonCodes.InvalidArguments, EllaiToolReasonCodes.ConsentMissing,
                        EllaiToolReasonCodes.DeniedByPolicy, EllaiToolReasonCodes.ResultTooLarge,
                        EllaiToolReasonCodes.Unavailable
                    }
                }
            },
            ["required"] = new[] { "toolName", "allowed", "denialReason", "evidence", "resultSummary", "itemCount", "truncated", "reasonCode" },
            ["additionalProperties"] = false
        };
        return JsonSerializer.Serialize(schema);
    }

    private static ToolContract Contract(string toolName, params (string Name, bool Required)[] arguments) =>
        new(new ReadOnlyDictionary<string, ToolArgumentContract>(arguments.ToDictionary(
            argument => argument.Name,
            argument => new ToolArgumentContract(argument.Name, argument.Required,
                argument.Name == "observations_json" ? ObservationJsonLength : ReferenceLength,
                ArgumentDescriptions[argument.Name], GetAllowedValues(toolName, argument.Name),
                MinimumLength: 1, Pattern: GetPattern(toolName, argument.Name)),
            StringComparer.Ordinal)));

    private static IReadOnlyList<string>? GetAllowedValues(string toolName, string argumentName) =>
        (toolName, argumentName) switch
        {
            (EllaiToolNames.State, "projection") => Array.AsReadOnly(new[] { "feature", "queue" }),
            (EllaiToolNames.State, "workflow") => Array.AsReadOnly(new[] { "Reported", "UnderReview", "Active", "AwaitingFollowUp", "Resolved" }),
            (EllaiToolNames.State, "sort") => Array.AsReadOnly(new[] { "latest-reported", "latest-updated" }),
            (EllaiToolNames.Evidence, "group_by") => Array.AsReadOnly(new[] { "region", "locality", "reported-day" }),
            (EllaiToolNames.Evidence, "classification_filter") => Array.AsReadOnly(new[] { "Classified", "Unclassified" }),
            _ => AllowedValues.GetValueOrDefault(argumentName)
        };

    private static string? GetPattern(string toolName, string argumentName) =>
        (toolName, argumentName) switch
        {
            (EllaiToolNames.State, "projection") => "^(feature|queue)$",
            (EllaiToolNames.State, "country") or (EllaiToolNames.Evidence, "country") or
                (EllaiToolNames.Locations, "country") or (EllaiToolNames.Imports, "country") => "^[A-Z]{2}$",
            (EllaiToolNames.State, "limit") or (EllaiToolNames.UiCatalogue, "limit") => "^([1-9]|1[0-9]|20)$",
            (EllaiToolNames.State, "reported_from") or (EllaiToolNames.State, "reported_before") => "^[0-9]{4}-[0-9]{2}-[0-9]{2}$",
            (EllaiToolNames.DocsSearch, "limit") => "^([1-9]|1[0-9]|20)$",
            (EllaiToolNames.Locations, "limit") => "^([1-9]|10)$",
            (EllaiToolNames.OwnedCaseCount, "window_days") => "^([1-9]|[1-9][0-9]|[12][0-9]{2}|3[0-5][0-9]|36[0-5])$",
            _ => null
        };
}

public sealed record ToolContract(IReadOnlyDictionary<string, ToolArgumentContract> Arguments);

public sealed record ToolArgumentContract(
    string Name, bool Required, int MaxLength, string Description,
    IReadOnlyList<string>? AllowedValues = null, int MinimumLength = 0, string? Pattern = null);
