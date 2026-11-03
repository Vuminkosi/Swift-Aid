using System.Collections.ObjectModel;
using System.Text.Json;

namespace SwiftAid.Application.Ellai;

/// <summary>Canonical, versioned argument contracts for model-visible reads.
/// Values are strings on the model-turn wire; handlers enforce domain-specific
/// formats and authorization after this structural validation.</summary>
public static class EllaiToolContractCatalog
{
    public const int CurrentSchemaVersion = 1;

    private const int ReferenceLength = 120;
    private const int ObservationJsonLength = 16_384;

    private static readonly IReadOnlyDictionary<string, string> ArgumentDescriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projection"] = "Requested state projection.",
            ["limit"] = "Maximum number of returned items, subject to the tool's narrower limit.",
            ["ref"] = "Exact bounded reference or topic text.",
            ["country"] = "Uppercase ISO 3166-1 alpha-2 country code.",
            ["workflow"] = "Recorded case workflow filter.",
            ["sort"] = "Supported queue ordering.",
            ["field"] = "Optional country or workflow field selector for case-results.",
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
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedValues =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["field"] = Array.AsReadOnly(new[] { "country", "workflow" })
        };

    private static readonly IReadOnlyDictionary<string, ToolContract> Contracts =
        new ReadOnlyDictionary<string, ToolContract>(new Dictionary<string, ToolContract>(StringComparer.Ordinal)
        {
            [EllaiToolNames.State] = Contract(
                ("projection", false), ("limit", false),
                ("country", false), ("workflow", false), ("sort", false)),
            [EllaiToolNames.Evidence] = Contract(("ref", true), ("field", false)),
            [EllaiToolNames.Topic] = Contract(("ref", true)),
            [EllaiToolNames.Capabilities] = Contract(("feature", false)),
            [EllaiToolNames.Receipts] = Contract(("receipt", false)),
            [EllaiToolNames.ProductFacts] = Contract(("topic", false)),
            [EllaiToolNames.Now] = Contract(),
            [EllaiToolNames.DocsSearch] = Contract(("q", true), ("limit", false)),
            [EllaiToolNames.DocsGet] = Contract(("section", true)),
            [EllaiToolNames.UiCatalogue] = Contract(("feature", false), ("goal", false), ("limit", false)),
            [EllaiToolNames.UiObserve] = Contract(("controls", true), ("view", true), ("context", false), ("feature", false)),
            [EllaiToolNames.IngestionSchema] = Contract(),
            [EllaiToolNames.ValidateObservations] = Contract(("observations_json", true)),
            [EllaiToolNames.Locations] = Contract(("q", true), ("country", false), ("limit", false)),
            [EllaiToolNames.ImportDestinations] = Contract(),
            [EllaiToolNames.Imports] = Contract(("workspace_id", true), ("organization_id", true), ("country", true)),
            [EllaiToolNames.ImportStatus] = Contract(("import_id", true)),
            [EllaiToolNames.ClassificationEvidence] = Contract(("case_id", true)),
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

            if (argument.AllowedValues is { Count: > 0 } values && !values.Contains(value, StringComparer.Ordinal))
            {
                error = $"Argument '{name}' has an unsupported value.";
                return false;
            }
            if (toolName == EllaiToolNames.Evidence && name == "ref" &&
                !string.Equals(value, EllaiCaseResultMemory.Reference, StringComparison.Ordinal))
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
                    ["maxLength"] = argument.MaxLength,
                    ["description"] = argument.Description
                };
                if (argument.AllowedValues is { Count: > 0 })
                    property["enum"] = argument.AllowedValues;
                else if (toolName == EllaiToolNames.Evidence && argument.Name == "ref")
                    property["enum"] = new[] { EllaiCaseResultMemory.Reference };
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

    private static ToolContract Contract(params (string Name, bool Required)[] arguments) =>
        new(new ReadOnlyDictionary<string, ToolArgumentContract>(arguments.ToDictionary(
            argument => argument.Name,
            argument => new ToolArgumentContract(argument.Name, argument.Required,
                argument.Name == "observations_json" ? ObservationJsonLength : ReferenceLength,
                ArgumentDescriptions[argument.Name], AllowedValues.GetValueOrDefault(argument.Name)),
            StringComparer.Ordinal)));
}

public sealed record ToolContract(IReadOnlyDictionary<string, ToolArgumentContract> Arguments);

public sealed record ToolArgumentContract(
    string Name, bool Required, int MaxLength, string Description, IReadOnlyList<string>? AllowedValues = null);
