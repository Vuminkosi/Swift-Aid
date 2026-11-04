using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Creates the official chat-completions adapter for the configured Meta route.</summary>
public static class EllaiMuseChatClientFactory
{
    internal static bool HasDuplicateArguments(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            return document.RootElement.EnumerateObject().Any(property => !keys.Add(property.Name));
        }
        catch (JsonException) { return false; }
    }

    public static IChatClient Create(EllaiOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.HasReasoningRoute || options.UsesOllamaBackend || options.UsesServerlessBackend)
            throw new MetaConfigurationException("Ellai Meta reasoning route is not configured.");

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.ApiUrl.TrimEnd('/') + "/", UriKind.Absolute)
        };
        if (httpClient is not null)
            clientOptions.Transport = new HttpClientPipelineTransport(httpClient);

        return new StreamedToolNameClient(new ChatClient(options.ReasoningModel, new ApiKeyCredential(options.ApiKey), clientOptions)
            .AsIChatClient());
    }

    /// <summary>Uses the configured, explicitly enabled serverless chat-completions route.</summary>
    public static IChatClient CreateServerless(EllaiOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.HasServerlessRoute)
            throw new MetaConfigurationException("Ellai serverless reasoning route is not configured.");
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.ServerlessEndpoint.TrimEnd('/') + "/", UriKind.Absolute)
        };
        var transport = new HttpClient(new CompatibleRequestHandler(httpClient, stripAuthorization: false));
        clientOptions.Transport = new HttpClientPipelineTransport(transport);
        return new CompatibleChatClient(new ChatClient(options.ServerlessModel,
            new ApiKeyCredential(options.ServerlessApiKey), clientOptions).AsIChatClient(), transport, streaming: false);
    }

    /// <summary>Uses the configured Ollama server's OpenAI-compatible chat interface.</summary>
    public static IChatClient CreateOllama(EllaiOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.HasOllamaRoute)
            throw new MetaConfigurationException("Ellai Ollama reasoning route is not configured.");
        var transport = new HttpClient(new CompatibleRequestHandler(httpClient, stripAuthorization: true));
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.OllamaEndpoint.TrimEnd('/') + "/v1/", UriKind.Absolute),
            Transport = new HttpClientPipelineTransport(transport)
        };
        return new StreamedToolNameClient(new CompatibleChatClient(new ChatClient(options.OllamaModel,
            new ApiKeyCredential("unused"), clientOptions).AsIChatClient(), transport, streaming: true));
    }

    // The official adapter assembles argument fragments but retains only the first
    // name fragment. Use its raw typed updates to correct that field before the
    // framework receives its completed function call; never infer an identity.
    private sealed class StreamedToolNameClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var names = new Dictionary<int, StreamedToolName>();
            var argumentBytes = 0;
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (update.RawRepresentation is StreamingChatCompletionUpdate raw)
                {
                    foreach (var part in raw.ToolCallUpdates)
                    {
                        if (!names.TryGetValue(part.Index, out var name)) names[part.Index] = name = new();
                        if (!string.IsNullOrEmpty(part.ToolCallId))
                        {
                            if (name.CallId is not null && name.CallId != part.ToolCallId)
                                throw new InvalidDataException("Conflicting streamed tool-call identities.");
                            name.CallId = part.ToolCallId;
                        }
                        if (!string.IsNullOrEmpty(part.FunctionName))
                        {
                            if (name.Value.Length + part.FunctionName.Length > 128)
                                throw new InvalidDataException("Streamed tool-call name exceeds the permitted length.");
                            name.First ??= part.FunctionName;
                            name.Value.Append(part.FunctionName);
                            name.Parts++;
                        }
                        if (part.FunctionArgumentsUpdate is { } arguments)
                        {
                            var bytes = arguments.ToMemory();
                            if (bytes.Length > 512_000 - argumentBytes)
                                throw new InvalidDataException("Streamed tool arguments exceed the permitted length.");
                            argumentBytes += bytes.Length;
                            name.Arguments.Write(bytes.Span);
                        }
                    }
                }
                for (var index = 0; index < update.Contents.Count; index++)
                {
                    if (update.Contents[index] is not FunctionCallContent call) continue;
                    var matches = names.Values.Where(name => name.CallId is not null && name.CallId == call.CallId).ToArray();
                    if (matches.Length > 1 || matches.Length == 0 && names.Values.Any(name =>
                            name.Parts > 1 || HasDuplicateArguments(name.Arguments.ToArray())))
                        throw new InvalidDataException("Ambiguous streamed tool-call identity.");
                    if (matches is not [var match]) continue;
                    var duplicate = HasDuplicateArguments(match.Arguments.ToArray());
                    if (match.Parts <= 1 && !duplicate) continue;
                    var completeName = match.Value.ToString();
                    if (match.Parts > 1 && call.Name != completeName && call.Name != match.First)
                        throw new InvalidDataException("Inconsistent streamed tool-call name.");
                    if (match.Parts <= 1) completeName = call.Name;
                    update.Contents[index] = new FunctionCallContent(call.CallId, completeName, call.Arguments)
                    {
                        Exception = duplicate ? new ArgumentException("Duplicate tool arguments.") : call.Exception,
                        InformationalOnly = call.InformationalOnly,
                        RawRepresentation = call.RawRepresentation,
                        AdditionalProperties = call.AdditionalProperties,
                        Annotations = call.Annotations
                    };
                }
                yield return update;
            }
        }

        private sealed class StreamedToolName
        {
            public string? CallId;
            public string? First;
            public StringBuilder Value { get; } = new();
            public MemoryStream Arguments { get; } = new();
            public int Parts;
        }
    }

    // Serverless completes before publishing updates; Ollama supports streaming.
    private sealed class CompatibleChatClient(IChatClient inner, HttpClient transport, bool streaming) : DelegatingChatClient(inner)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (streaming)
            {
                await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                    yield return update;
                yield break;
            }
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) transport.Dispose();
        }
    }

    // The existing provider contracts use max_tokens; the SDK emits
    // max_completion_tokens. Ollama requests also must carry no credentials.
    private sealed class CompatibleRequestHandler : HttpMessageHandler
    {
        private readonly HttpClient _downstream;
        private readonly bool _ownsDownstream;
        private readonly bool _stripAuthorization;

        public CompatibleRequestHandler(HttpClient? downstream, bool stripAuthorization)
        {
            _ownsDownstream = downstream is null;
            _downstream = downstream ?? new HttpClient();
            _stripAuthorization = stripAuthorization;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };
            foreach (var header in request.Headers)
                if (!_stripAuthorization || !string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                    forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            foreach (var option in request.Options)
                forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            if (request.Content is not null)
            {
                var original = request.Content;
                var json = JsonNode.Parse(await original.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject
                    ?? throw new InvalidDataException("Invalid serverless chat request.");
                if (json.TryGetPropertyValue("max_completion_tokens", out var maximum))
                {
                    json.Remove("max_completion_tokens");
                    json["max_tokens"] = maximum;
                }
                var replacement = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
                foreach (var header in original.Headers)
                    if (!string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                        replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                forwarded.Content = replacement;
            }
            return await _downstream.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsDownstream) _downstream.Dispose();
            base.Dispose(disposing);
        }
    }
}
