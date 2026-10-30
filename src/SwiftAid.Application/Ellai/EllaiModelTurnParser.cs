using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Outcome of parsing one provider text turn. A null <see cref="Turn"/>
/// carries a safe repair hint in <see cref="Error"/> that the loop feeds back
/// to the model; the hint never executes anything.</summary>
public sealed record ParsedModelTurn(EllaiModelTurn? Turn, string? Error);

/// <summary>Strict turn-protocol parser: provider text must be exactly one
/// JSON object holding either <c>tool_calls</c> or <c>final_decision</c>.
/// Tool names resolve through the broker's canonical registry (aliases
/// included); unknown tools, non-string args and malformed decisions are
/// repairable errors, never silent drops or invented calls. Final decisions
/// still pass <see cref="EllaiDecisionValidator"/> in the runner.</summary>
public static class EllaiModelTurnParser
{
    private static readonly JsonSerializerOptions Json = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static ParsedModelTurn Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ParsedModelTurn(null, "The reply was empty.");
        }

        var payload = StripFence(text.Trim());
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return new ParsedModelTurn(null, "The reply was not a JSON object.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ParsedModelTurn(null, "The reply must be one JSON object.");
            }

            var root = document.RootElement;
            var hasCalls = HasProperty(root, "tool_calls");
            var hasDecision = HasProperty(root, "final_decision");
            if (hasCalls == hasDecision)
            {
                return new ParsedModelTurn(null,
                    "The reply must contain exactly one of tool_calls or final_decision.");
            }

            return hasCalls ? ParseCalls(root) : ParseDecision(root);
        }
    }

    private static ParsedModelTurn ParseCalls(JsonElement root)
    {
        var calls = GetProperty(root, "tool_calls");
        if (calls.ValueKind != JsonValueKind.Array)
        {
            return new ParsedModelTurn(null, "tool_calls must be an array.");
        }

        var requests = new List<EllaiToolRequest>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object)
            {
                return new ParsedModelTurn(null, "Each tool call must be an object.");
            }

            if (!TryGetString(call, "id", out var id) || string.IsNullOrWhiteSpace(id) || id.Length > 64)
            {
                return new ParsedModelTurn(null, "Each tool call needs an id of 1..64 characters.");
            }

            if (!ids.Add(id))
            {
                return new ParsedModelTurn(null, $"Duplicate tool call id '{id}'.");
            }

            if (!TryGetString(call, "name", out var name) || string.IsNullOrWhiteSpace(name))
            {
                return new ParsedModelTurn(null, "Each tool call needs a name.");
            }

            if (!EllaiToolBroker.TryResolveCanonical(name, out var canonical))
            {
                return new ParsedModelTurn(null, $"Unknown tool '{name}'.");
            }

            if (!call.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
            {
                return new ParsedModelTurn(null, $"Tool call '{id}' needs an args object.");
            }

            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var argument in args.EnumerateObject())
            {
                if (argument.Value.ValueKind != JsonValueKind.String)
                {
                    return new ParsedModelTurn(null,
                        $"Tool call '{id}' argument '{argument.Name}' must be a string.");
                }

                arguments[argument.Name] = argument.Value.GetString() ?? string.Empty;
            }

            if (arguments.Count > 8)
            {
                return new ParsedModelTurn(null, $"Tool call '{id}' carries too many arguments.");
            }

            requests.Add(new EllaiToolRequest(canonical, arguments, id));
        }

        if (requests.Count == 0 || requests.Count > EllaiCycleRunner.MaxToolCalls)
        {
            return new ParsedModelTurn(null,
                $"tool_calls must name 1..{EllaiCycleRunner.MaxToolCalls} calls.");
        }

        return new ParsedModelTurn(new EllaiModelTurn(requests, null), null);
    }

    private static ParsedModelTurn ParseDecision(JsonElement root)
    {
        var element = GetProperty(root, "final_decision");
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new ParsedModelTurn(null, "final_decision must be an object.");
        }

        try
        {
            var decision = JsonSerializer.Deserialize<EllaiDecision>(element.GetRawText(), Json);
            if (decision is null)
            {
                return new ParsedModelTurn(null, "final_decision was empty.");
            }

            return new ParsedModelTurn(new EllaiModelTurn(
                Array.Empty<EllaiToolRequest>(), decision), null);
        }
        catch (JsonException)
        {
            return new ParsedModelTurn(null, "final_decision is not a valid EllaiDecision.");
        }
    }

    private static string StripFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLine = text.IndexOf('\n');
        var body = firstLine < 0 ? string.Empty : text[(firstLine + 1)..];
        var fence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fence < 0 ? body : body[..fence]).Trim();
    }

    private static bool HasProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return default;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString() ?? string.Empty;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
