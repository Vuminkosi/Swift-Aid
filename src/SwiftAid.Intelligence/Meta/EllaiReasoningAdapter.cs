using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Provider-reported usage for one reasoning call.</summary>
public sealed record EllaiReasoningUsage(int PromptTokens, int CompletionTokens, int TotalTokens, int ReasoningTokens);

/// <summary>Verified reasoning result: completed text plus usage. Usage is
/// always returned when the provider supplies it, including for discarded
/// attempts reconciled by the caller.</summary>
public sealed record EllaiReasoningResult(string Text, string Model, string FinishReason, EllaiReasoningUsage Usage);

/// <summary>Live Meta reasoning adapter (ELL-T05/T06). Wraps HttpClient only;
/// the API key travels exclusively in the Authorization header and never
/// appears in exceptions, logs or results. Only synthetic or consented
/// non-sensitive prompts may be sent — enforced by the caller policy.</summary>
public sealed class EllaiReasoningAdapter(HttpClient http, EllaiOptions options)
{
    public async Task<EllaiReasoningResult> CompleteAsync(
        IReadOnlyList<(string Role, string Content)> messages, int maxTokens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (!options.HasReasoningRoute)
        {
            throw new MetaConfigurationException("Ellai reasoning route is not configured.");
        }

        if (messages.Count == 0 || maxTokens is < 1 or > 16384)
        {
            throw new ArgumentException("A non-empty message list and 1..16384 max tokens are required.");
        }

        var payload = new
        {
            model = options.ReasoningModel,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToList(),
            max_tokens = maxTokens
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, options.ApiUrl.TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
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
            throw new InvalidOperationException($"Ellai reasoning transport failed ({ex.GetType().Name}).");
        }

        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"Ellai reasoning refused the request (HTTP {status}).");
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
                : string.Empty;
            var usage = root.TryGetProperty("usage", out var u) ? u : (JsonElement?)null;
            var (prompt, completion, total, reasoning) = ReadUsage(usage);
            return new EllaiReasoningResult(text, model, finish,
                new EllaiReasoningUsage(prompt, completion, total, reasoning));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidOperationException("Ellai reasoning returned an unreadable envelope.");
        }
    }

    private static (int Prompt, int Completion, int Total, int Reasoning) ReadUsage(JsonElement? usage)
    {
        if (usage is null || usage.Value.ValueKind != JsonValueKind.Object)
        {
            return (0, 0, 0, 0);
        }

        var u = usage.Value;
        var prompt = GetInt(u, "prompt_tokens");
        var completion = GetInt(u, "completion_tokens");
        var total = GetInt(u, "total_tokens");
        var reasoning = 0;
        if (u.TryGetProperty("completion_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            reasoning = GetInt(details, "reasoning_tokens");
        }

        return (prompt, completion, total, reasoning);
    }

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
