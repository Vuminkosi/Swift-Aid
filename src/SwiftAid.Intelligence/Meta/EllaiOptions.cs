using System.Globalization;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Ellai provider settings, bound from the <c>Ellai</c> configuration
/// section. Environment mapping accepts both <c>Ellai__X</c> (standard) and
/// the legacy single-underscore <c>Ellai_X</c> form for top-level flags.
/// The API key is never logged, never returned, and only leaves the server
/// inside an Authorization header to the configured provider URL.</summary>
public sealed class EllaiOptions
{
    public bool Enabled { get; init; }

    public string ApiKey { get; init; } = string.Empty;

    public string ApiUrl { get; init; } = string.Empty;

    public string ReasoningModel { get; init; } = string.Empty;

    public string AsrModel { get; init; } = string.Empty;

    public string AsrLanguage { get; init; } = "en";

    public string AsrWebSocketUrl { get; init; } = string.Empty;

    public string AsrTranscriptionUrl { get; init; } = string.Empty;

    public string AsrMode { get; init; } = "DIARIZATION";

    /// <summary>Reasoning backend selector: <c>meta</c> (default, Meta-hosted
    /// API), <c>ollama</c> (local open-weight, e.g. llama3.2:3b) or
    /// <c>serverless</c> (future OpenAI-compatible cloud serverless endpoint).
    /// Configured via <c>Ellai__ReasoningBackend</c>; unknown values fail
    /// closed to <c>meta</c> semantics.</summary>
    public string ReasoningBackend { get; init; } = "meta";

    /// <summary>Local Ollama base URL, e.g. http://localhost:11434. No secret
    /// is ever attached to this route.</summary>
    public string OllamaEndpoint { get; init; } = string.Empty;

    /// <summary>Local open-weight model id. Default is llama3.2:3b, the model
    /// pinned for local-first reasoning until a cloud lane is approved.</summary>
    public string OllamaModel { get; init; } = "llama3.2:3b";

    /// <summary>Future cloud-serverless OpenAI-compatible base URL
    /// (chat/completions). Empty until the operator provisions it.</summary>
    public string ServerlessEndpoint { get; init; } = string.Empty;

    public string ServerlessModel { get; init; } = string.Empty;

    public string ServerlessApiKey { get; init; } = string.Empty;

    /// <summary>Master switch for the self-hosted/cloud-serverless lane.
    /// Stays <c>false</c> until the Meta-hosted default is fully tested;
    /// the router refuses the serverless lane while this is false, even
    /// when endpoint/model/key are all present.</summary>
    public bool ServerlessEnabled { get; init; }

    /// <summary>Operator-funded dollar budget for everything ($70).
    /// Global kill switch and per-envelope bound in the usage ledger.</summary>
    public decimal SpendingCapDollars { get; init; } = 70.00m;

    /// <summary>Rate per 1,000 input tokens in dollars. Zero means unpriced:
    /// dollar caps stay inert until the operator sets real Meta plan rates.</summary>
    public decimal PricingInputPer1KTokens { get; init; }

    public decimal PricingOutputPer1KTokens { get; init; }

    public decimal PricingAudioPerMinute { get; init; }

    public bool HasPricing => PricingInputPer1KTokens > 0
        || PricingOutputPer1KTokens > 0
        || PricingAudioPerMinute > 0;

    /// <summary>Runtime policy file path. Absolute as-is; relative paths
    /// resolve against the content root (walking up for local layouts) or
    /// the app base. In Docker this is the read-only bind mount, never
    /// content baked into the image.</summary>
    public string PolicyPath { get; init; } = "docs/ellai/ellai-system-instruction.md";

    /// <summary>Pinned approved policy version. Guarded calls stamp and
    /// verify this label alongside the file hash.</summary>
    public string PolicyVersion { get; init; } = "1.6";

    public bool HasReasoningRoute =>
        Enabled && !string.IsNullOrWhiteSpace(ApiKey)
        && IsHttpsUrl(ApiUrl) && !string.IsNullOrWhiteSpace(ReasoningModel);

    public bool HasUploadAsrRoute =>
        Enabled && !string.IsNullOrWhiteSpace(ApiKey)
        && IsHttpsUrl(AsrTranscriptionUrl) && !string.IsNullOrWhiteSpace(AsrModel);

    public bool HasRealtimeAsrRoute =>
        Enabled && !string.IsNullOrWhiteSpace(ApiKey)
        && IsWebSocketUrl(AsrWebSocketUrl) && !string.IsNullOrWhiteSpace(AsrModel);

    public bool UsesOllamaBackend =>
        Enabled && string.Equals(ReasoningBackend, "ollama", StringComparison.OrdinalIgnoreCase);

    public bool UsesServerlessBackend =>
        Enabled && string.Equals(ReasoningBackend, "serverless", StringComparison.OrdinalIgnoreCase);

    public bool HasOllamaRoute =>
        UsesOllamaBackend && IsHttpUrl(OllamaEndpoint) && !string.IsNullOrWhiteSpace(OllamaModel);

    public bool HasServerlessRoute =>
        ServerlessEnabled && UsesServerlessBackend && !string.IsNullOrWhiteSpace(ServerlessApiKey)
        && IsHttpsUrl(ServerlessEndpoint) && !string.IsNullOrWhiteSpace(ServerlessModel);

    public static EllaiOptions Bind(Func<string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);

        // Providers may use Ellai:X, Ellai__X or Ellai_X spellings; match
        // case-insensitively with underscores ignored.
        var seen = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? Raw(string key)
        {
            if (!seen.TryGetValue(key, out var value))
            {
                value = get(key);
                seen[key] = value;
            }

            return value;
        }

        string? First(params string[] keys)
        {
            foreach (var key in keys)
            {
                var direct = Raw(key);
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    return direct.Trim();
                }
            }

            return null;
        }

        string? Setting(string suffix, params string[] extra)
        {
            var candidates = new List<string>
            {
                "Ellai:" + suffix,
                "Ellai__" + suffix.Replace(":", "__"),
                "Ellai_" + suffix.Replace(":", "_"),
            };
            candidates.AddRange(extra);
            return First(candidates.ToArray());
        }

        decimal? DecimalSetting(string suffix, params string[] extra)
        {
            var raw = Setting(suffix, extra);
            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                && value >= 0
                ? value
                : null;
        }

        var enabledRaw = Setting("Enabled");
        return new EllaiOptions
        {
            Enabled = string.Equals(enabledRaw, "true", StringComparison.OrdinalIgnoreCase),
            ApiKey = Setting("ApiKey") ?? string.Empty,
            ApiUrl = Setting("ApiUrl") ?? string.Empty,
            ReasoningModel = Setting("ReasoningModel") ?? string.Empty,
            AsrModel = Setting("AsrModel", "Ellai__ASR_MODEL", "Ellai:ASR_MODEL") ?? string.Empty,
            AsrLanguage = Setting("Asr:Language") ?? "en",
            AsrWebSocketUrl = Setting("Asr:Wss", "Ellai__ASR_WSS", "Ellai:ASR_WSS") ?? string.Empty,
            AsrTranscriptionUrl = Setting(
                "Asr:Transcription:Url",
                "Ellai__ASR_Transcription__URL",
                "Ellai:ASR_Transcription:URL") ?? string.Empty,
            AsrMode = Setting("Asr:Mode", "Ellai__ASRMode", "Ellai:ASRMode") ?? "DIARIZATION",
            ReasoningBackend = Setting("ReasoningBackend", "Ellai__ReasoningBackend") ?? "meta",
            OllamaEndpoint = Setting("Reasoning:Ollama:Endpoint", "Ellai__Reasoning__Ollama__Endpoint", "Ellai__Ollama__Endpoint") ?? string.Empty,
            OllamaModel = Setting("Reasoning:Ollama:Model", "Ellai__Reasoning__Ollama__Model", "Ellai__Ollama__Model") ?? "llama3.2:3b",
            ServerlessEndpoint = Setting("Reasoning:Serverless:Endpoint", "Ellai__Reasoning__Serverless__Endpoint") ?? string.Empty,
            ServerlessModel = Setting("Reasoning:Serverless:Model", "Ellai__Reasoning__Serverless__Model") ?? string.Empty,
            ServerlessApiKey = Setting("Reasoning:Serverless:ApiKey", "Ellai__Reasoning__Serverless__ApiKey") ?? string.Empty,
            ServerlessEnabled = string.Equals(
                Setting("Reasoning:Serverless:Enabled", "Ellai__Reasoning__Serverless__Enabled"),
                "true", StringComparison.OrdinalIgnoreCase),
            SpendingCapDollars = DecimalSetting("Pricing:SpendingCap", "Ellai__Pricing__SpendingCap") ?? 70.00m,
            PricingInputPer1KTokens = DecimalSetting("Pricing:InputPer1K", "Ellai__Pricing__InputPer1K") ?? 0,
            PricingOutputPer1KTokens = DecimalSetting("Pricing:OutputPer1K", "Ellai__Pricing__OutputPer1K") ?? 0,
            PricingAudioPerMinute = DecimalSetting("Pricing:AudioPerMinute", "Ellai__Pricing__AudioPerMinute") ?? 0,
            PolicyPath = Setting("Policy:Path", "Ellai__Policy__Path") ?? "docs/ellai/ellai-system-instruction.md",
            PolicyVersion = Setting("Policy:Version", "Ellai__Policy__Version") ?? "1.6",
        };
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enabled)
        {
            errors.Add("Ellai is not enabled.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            errors.Add("Ellai API key is missing.");
        }

        if (!IsHttpsUrl(ApiUrl))
        {
            errors.Add("Ellai API URL must be an https URL.");
        }

        if (string.IsNullOrWhiteSpace(ReasoningModel))
        {
            errors.Add("Ellai reasoning model is missing.");
        }

        if (string.IsNullOrWhiteSpace(AsrModel))
        {
            errors.Add("Ellai ASR model is missing.");
        }

        if (!IsHttpsUrl(AsrTranscriptionUrl))
        {
            errors.Add("Ellai ASR transcription URL must be an https URL.");
        }

        if (!IsWebSocketUrl(AsrWebSocketUrl))
        {
            errors.Add("Ellai ASR WebSocket URL must be a wss URL.");
        }

        return errors;
    }

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsWebSocketUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == "wss" || uri.Scheme == "ws");
}
