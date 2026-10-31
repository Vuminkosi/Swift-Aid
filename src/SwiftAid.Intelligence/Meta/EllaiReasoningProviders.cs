using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Backend-agnostic reasoning contract for Ellai (ELL-T05 extension).
/// Implementations: Meta-hosted API, local Ollama open-weight (default
/// llama3.2:3b), and a future OpenAI-compatible cloud-serverless endpoint.
/// Callers enforce the synthetic/consented non-sensitive policy; no provider
/// receives secrets, case narratives or original audio beyond the prompt.</summary>
public interface IEllaiReasoningProvider
{
    string Backend { get; }

    string Model { get; }

    Task<EllaiReasoningResult> CompleteAsync(
        IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct);
}

/// <summary>Meta-hosted reasoning lane. Key travels in the Authorization
/// header only and never appears in exceptions, logs or results.</summary>
public sealed class MetaEllaiReasoningProvider(
    EllaiReasoningAdapter inner, EllaiOptions options) : IEllaiReasoningProvider
{
    public string Backend => "meta";

    public string Model => options.ReasoningModel;

    public Task<EllaiReasoningResult> CompleteAsync(
        IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct) =>
        inner.CompleteAsync(messages, maxTokens, ct);
}

/// <summary>Local Ollama open-weight lane (default model llama3.2:3b). Talks
/// to <c>{endpoint}/api/chat</c> with <c>stream:false</c>. No credential is
/// attached; loopback http is accepted, remote https preferred.</summary>
public sealed class OllamaEllaiReasoningProvider(HttpClient http, EllaiOptions options) : IEllaiReasoningProvider
{
    public string Backend => "ollama";

    public string Model => options.OllamaModel;

    public async Task<EllaiReasoningResult> CompleteAsync(
        IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (!options.HasOllamaRoute)
        {
            throw new MetaConfigurationException("Ellai Ollama reasoning route is not configured.");
        }

        if (messages.Count == 0 || maxTokens is < 1 or > 16384)
        {
            throw new ArgumentException("A non-empty message list and 1..16384 max tokens are required.");
        }

        var payload = new
        {
            model = options.OllamaModel,
            stream = false,
            options = new { num_predict = maxTokens },
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToList(),
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, options.OllamaEndpoint.TrimEnd('/') + "/api/chat")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        int status;
        string body;
        try
        {
            using var response = await http.SendAsync(request, ct);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Ellai Ollama transport failed ({ex.GetType().Name}).");
        }

        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"Ellai Ollama refused the request (HTTP {status}).");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var text = string.Empty;
            if (root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString() ?? string.Empty;
            }

            var finish = root.TryGetProperty("done_reason", out var dr) && dr.ValueKind == JsonValueKind.String
                ? dr.GetString() ?? string.Empty
                : string.Empty;
            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? string.Empty
                : options.OllamaModel;
            var prompt = GetInt(root, "prompt_eval_count");
            var completion = GetInt(root, "eval_count");
            return new EllaiReasoningResult(text, model, finish,
                new EllaiReasoningUsage(prompt, completion, prompt + completion, 0));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException("Ellai Ollama returned an unreadable envelope.");
        }
    }

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}

/// <summary>Future cloud-serverless lane: OpenAI-compatible
/// <c>{endpoint}/chat/completions</c> with a bearer key. Registered only when
/// the operator provisions an https endpoint, model and key; otherwise the
/// router fails closed and the local Ollama lane stays the default.</summary>
public sealed class ServerlessEllaiReasoningProvider(HttpClient http, EllaiOptions options) : IEllaiReasoningProvider
{
    public string Backend => "serverless";

    public string Model => options.ServerlessModel;

    public async Task<EllaiReasoningResult> CompleteAsync(
        IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (!options.HasServerlessRoute)
        {
            throw new MetaConfigurationException("Ellai serverless reasoning route is not configured.");
        }

        if (messages.Count == 0 || maxTokens is < 1 or > 16384)
        {
            throw new ArgumentException("A non-empty message list and 1..16384 max tokens are required.");
        }

        var payload = new
        {
            model = options.ServerlessModel,
            messages = messages.Select(MetaCompatibleChatMessage.ToPayload).ToList(),
            max_tokens = maxTokens,
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, options.ServerlessEndpoint.TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServerlessApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        int status;
        string body;
        try
        {
            using var response = await http.SendAsync(request, ct);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Ellai serverless transport failed ({ex.GetType().Name}).");
        }

        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"Ellai serverless refused the request (HTTP {status}).");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var choice = root.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var text = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : string.Empty;
            var finish = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
                ? fr.GetString() ?? string.Empty
                : string.Empty;
            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? string.Empty
                : options.ServerlessModel;
            return new EllaiReasoningResult(text, model, finish,
                new EllaiReasoningUsage(0, 0, 0, 0));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidOperationException("Ellai serverless returned an unreadable envelope.");
        }
    }
}

/// <summary>Maps validated Ellai turns to OpenAI-compatible provider message
/// history. Tool outputs are correlated with the assistant calls that caused
/// them; raw model text and tool observations remain separate.</summary>
internal static class MetaCompatibleChatMessage
{
    public static object ToPayload(EllaiChatMessage message)
    {
        if (message.ToolCalls is { Count: > 0 })
        {
            if (!string.Equals(message.Role, "assistant", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Only an assistant turn can carry tool calls.");
            }

            return new
            {
                role = message.Role,
                content = (string?)null,
                tool_calls = message.ToolCalls.Select(call => new
                {
                    id = call.CallId ?? throw new InvalidOperationException("A tool call is missing its correlation id."),
                    type = "function",
                    function = new
                    {
                        name = call.ToolName,
                        arguments = JsonSerializer.Serialize(call.Arguments),
                    },
                }).ToList(),
            };
        }

        if (string.Equals(message.Role, "tool", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                throw new InvalidOperationException("A tool result is missing its correlation id.");
            }

            return new
            {
                role = message.Role,
                tool_call_id = message.ToolCallId,
                content = message.Content,
            };
        }

        return new { role = message.Role, content = message.Content };
    }
}

/// <summary>Selects the configured reasoning lane. Unknown backend values
/// fail closed to Meta semantics; unconfigured lanes throw
/// <see cref="MetaConfigurationException"/> instead of silently falling back,
/// so the capability policy always reflects the real route.</summary>
public sealed class EllaiReasoningRouter(
    EllaiOptions options,
    EllaiReasoningAdapter meta,
    OllamaEllaiReasoningProvider ollama,
    ServerlessEllaiReasoningProvider serverless,
    EllaiPromptBuilder? policyBuilder = null)
{
    public IEllaiReasoningProvider Resolve()
    {
        if (options.UsesOllamaBackend)
        {
            return ollama;
        }

        if (options.UsesServerlessBackend)
        {
            if (!options.ServerlessEnabled)
            {
                throw new MetaConfigurationException(
                    "The Ellai serverless lane is disabled until the Meta-hosted default is fully tested.");
            }

            return serverless;
        }

        return new MetaEllaiReasoningProvider(meta, options);
    }

    /// <summary>Mandatory guarded call: the pinned runtime policy becomes
    /// the system message on every call. Caller-supplied system messages
    /// are stripped so users cannot smuggle their own policy; without a
    /// loaded policy builder the call is refused outright.</summary>
    public Task<EllaiReasoningResult> CompleteGuardedAsync(
        EllaiRoleOverlay role,
        string capabilitySummary,
        string limitsSummary,
        IReadOnlyList<EllaiChatMessage> userMessages,
        int maxTokens,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(userMessages);
        if (policyBuilder is null)
        {
            throw new MetaConfigurationException(
                "Ellai runtime policy is not loaded; guarded reasoning is unavailable.");
        }

        var system = policyBuilder.Build(role, capabilitySummary, limitsSummary);
        var messages = new List<EllaiChatMessage> { new("system", system) };
        messages.AddRange(userMessages.Where(m =>
            !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)));
        return Resolve().CompleteAsync(messages, maxTokens, ct);
    }
}
