using System.Net;
using System.Text;
using System.Text.Json;
using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T05")]
public sealed class EllaiReasoningBackendTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public string? RequestBody { get; private set; }

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static EllaiOptions Bind(IReadOnlyDictionary<string, string?> values) =>
        EllaiOptions.Bind(key => values.TryGetValue(key, out var v) ? v : null);

    [Fact]
    public void DefaultsToMetaWithLocalOllamaModelPinned()
    {
        var options = Bind(new Dictionary<string, string?>());

        Assert.Equal("meta", options.ReasoningBackend);
        Assert.Equal("llama3.2:3b", options.OllamaModel);
        Assert.False(options.HasOllamaRoute);
        Assert.False(options.HasServerlessRoute);
    }

    [Fact]
    public void OllamaRouteBindsFromEnvironment()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ellai_Enabled"] = "true",
            ["Ellai__ReasoningBackend"] = "ollama",
            ["Ellai__Reasoning__Ollama__Endpoint"] = "http://localhost:11434",
            ["Ellai__Reasoning__Ollama__Model"] = "llama3.2:3b",
        });

        Assert.True(options.UsesOllamaBackend);
        Assert.True(options.HasOllamaRoute);
        Assert.False(options.HasServerlessRoute);
    }

    [Fact]
    public void ServerlessRouteRequiresHttpsModelAndKey()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ellai_Enabled"] = "true",
            ["Ellai__ReasoningBackend"] = "serverless",
            ["Ellai__Reasoning__Serverless__Enabled"] = "true",
            ["Ellai__Reasoning__Serverless__Endpoint"] = "https://inference.example/v1",
            ["Ellai__Reasoning__Serverless__Model"] = "open-weight-large",
            ["Ellai__Reasoning__Serverless__ApiKey"] = "k",
        });

        Assert.True(options.UsesServerlessBackend);
        Assert.True(options.HasServerlessRoute);
    }

    [Fact]
    public async Task OllamaProviderParsesChatEnvelope()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ellai_Enabled"] = "true",
            ["Ellai__ReasoningBackend"] = "ollama",
            ["Ellai__Reasoning__Ollama__Endpoint"] = "http://localhost:11434",
            ["Ellai__Reasoning__Ollama__Model"] = "llama3.2:3b",
        });
        const string json = """{"model":"llama3.2:3b","message":{"role":"assistant","content":"ok"},"done_reason":"stop","prompt_eval_count":9,"eval_count":3}""";
        using var http = new HttpClient(new StubHandler(json));
        var provider = new OllamaEllaiReasoningProvider(http, options);

        var result = await provider.CompleteAsync([new EllaiChatMessage("user", "Reply with the single word: ok")], 64, CancellationToken.None);

        Assert.Equal("ollama", provider.Backend);
        Assert.Equal("llama3.2:3b", result.Model);
        Assert.Contains("ok", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(12, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task OllamaStreamingProviderForwardsNdjsonChunksAndFinalUsage()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ellai_Enabled"] = "true",
            ["Ellai__ReasoningBackend"] = "ollama",
            ["Ellai__Reasoning__Ollama__Endpoint"] = "http://localhost:11434",
            ["Ellai__Reasoning__Ollama__Model"] = "llama3.2:3b",
        });
        const string body = "{\"model\":\"llama3.2:3b\",\"message\":{\"content\":\"part one\"},\"done\":false}\n" +
            "{\"message\":{\"content\":\" part two\"},\"done\":false}\n" +
            "{\"done\":true,\"prompt_eval_count\":10,\"eval_count\":4}\n";
        var handler = new StubHandler(body);
        using var http = new HttpClient(handler);
        var provider = new OllamaEllaiReasoningProvider(http, options);
        var deltas = new List<string>();

        var result = await provider.CompleteStreamingAsync(
            [new EllaiChatMessage("user", "hello")], 64,
            (text, _) => { deltas.Add(text); return ValueTask.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(["part one", " part two"], deltas);
        Assert.Equal("part one part two", result.Text);
        Assert.Equal(10, result.Usage.PromptTokens);
        Assert.Equal(4, result.Usage.CompletionTokens);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(request.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task MetaProviderSerializesCorrelatedToolCallsAndResults()
    {
        const string response = """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""";
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ellai__Enabled"] = "true",
            ["Ellai__ApiKey"] = "test-key",
            ["Ellai__ApiUrl"] = "https://api.meta.ai/v1",
            ["Ellai__ReasoningModel"] = "muse-spark-1.3-contributor",
        });
        var handler = new StubHandler(response);
        using var http = new HttpClient(handler);
        var adapter = new EllaiReasoningAdapter(http, options);
        EllaiChatMessage[] messages =
        [
            new("system", "policy"),
            new("assistant", "tool request", [new EllaiToolRequest(
                EllaiToolNames.Now, new Dictionary<string, string>(), "call-1")]),
            new("tool", "tool result", ToolCallId: "call-1"),
        ];

        await adapter.CompleteAsync(messages, 64, CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.RequestBody!);
        var wireMessages = payload.RootElement.GetProperty("messages");
        var assistant = wireMessages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("content").ValueKind);
        var call = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray().ToArray());
        Assert.Equal("call-1", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal(EllaiToolNames.Now, call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{}", call.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool", wireMessages[2].GetProperty("role").GetString());
        Assert.Equal("call-1", wireMessages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("tool result", wireMessages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task MetaStreamingAdapterForwardsTextDeltasAndFinalUsage()
    {
        const string body = "data: {\"model\":\"muse-spark\",\"choices\":[{\"delta\":{\"content\":\"part one\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" part two\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":5,\"total_tokens\":17}}\n\n" +
            "data: [DONE]\n\n";
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ellai__Enabled"] = "true",
            ["Ellai__ApiKey"] = "test-key",
            ["Ellai__ApiUrl"] = "https://api.meta.ai/v1",
            ["Ellai__ReasoningModel"] = "muse-spark-1.3-contributor",
        });
        var handler = new StubHandler(body);
        using var http = new HttpClient(handler);
        var adapter = new EllaiReasoningAdapter(http, options);
        var deltas = new List<string>();

        var result = await adapter.CompleteStreamingAsync(
            [new EllaiChatMessage("user", "hello")], 64,
            (text, _) => { deltas.Add(text); return ValueTask.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(["part one", " part two"], deltas);
        Assert.Equal("part one part two", result.Text);
        Assert.Equal(12, result.Usage.PromptTokens);
        Assert.Equal(5, result.Usage.CompletionTokens);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(request.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task MetaRefusalReportsOnlySafeProviderErrorIdentifiers()
    {
        const string body = """{"error":{"message":"private prompt and secret value","type":"invalid_request_error","param":"messages","code":"bad_messages"}}""";
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ellai__Enabled"] = "true",
            ["Ellai__ApiKey"] = "test-key",
            ["Ellai__ApiUrl"] = "https://api.meta.ai/v1",
            ["Ellai__ReasoningModel"] = "muse-spark-1.3-contributor",
        });
        using var http = new HttpClient(new StubHandler(body, HttpStatusCode.BadRequest));
        var adapter = new EllaiReasoningAdapter(http, options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CompleteAsync([new EllaiChatMessage("user", "synthetic request")], 64, CancellationToken.None));

        Assert.Contains("HTTP 400", exception.Message, StringComparison.Ordinal);
        Assert.Contains("code=bad_messages", exception.Message, StringComparison.Ordinal);
        Assert.Contains("type=invalid_request_error", exception.Message, StringComparison.Ordinal);
        Assert.Contains("param=messages", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private prompt", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OllamaProviderFailsClosedWhenUnconfigured()
    {
        var options = Bind(new Dictionary<string, string?> { ["Ellai_Enabled"] = "true" });
        using var http = new HttpClient(new StubHandler("{}"));
        var provider = new OllamaEllaiReasoningProvider(http, options);

        await Assert.ThrowsAsync<MetaConfigurationException>(() =>
            provider.CompleteAsync([new EllaiChatMessage("user", "hi")], 64, CancellationToken.None));
    }

    [Fact]
    public async Task ServerlessProviderFailsClosedWhenUnprovisioned()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ellai_Enabled"] = "true",
            ["Ellai__ReasoningBackend"] = "serverless",
        });
        using var http = new HttpClient(new StubHandler("{}"));
        var provider = new ServerlessEllaiReasoningProvider(http, options);

        await Assert.ThrowsAsync<MetaConfigurationException>(() =>
            provider.CompleteAsync([new EllaiChatMessage("user", "hi")], 64, CancellationToken.None));
    }

    [Fact]
    public void ServerlessLaneStaysDisabledWithoutExplicitOptIn()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ellai_Enabled"] = "true",
            ["Ellai__ReasoningBackend"] = "serverless",
            ["Ellai__Reasoning__Serverless__Endpoint"] = "https://inference.example/v1",
            ["Ellai__Reasoning__Serverless__Model"] = "open-weight-large",
            ["Ellai__Reasoning__Serverless__ApiKey"] = "k",
        });

        Assert.False(options.ServerlessEnabled);
        Assert.False(options.HasServerlessRoute);

        using var http = new HttpClient(new StubHandler("{}"));
        var router = new EllaiReasoningRouter(
            options,
            new EllaiReasoningAdapter(http, options),
            new OllamaEllaiReasoningProvider(http, options),
            new ServerlessEllaiReasoningProvider(http, options));

        Assert.Throws<MetaConfigurationException>(() => router.Resolve());
    }

    [Fact]
    public void ServerlessLaneOpensOnlyWithExplicitEnabledFlag()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ellai_Enabled"] = "true",
            ["Ellai__ReasoningBackend"] = "serverless",
            ["Ellai__Reasoning__Serverless__Enabled"] = "true",
            ["Ellai__Reasoning__Serverless__Endpoint"] = "https://inference.example/v1",
            ["Ellai__Reasoning__Serverless__Model"] = "open-weight-large",
            ["Ellai__Reasoning__Serverless__ApiKey"] = "k",
        });

        Assert.True(options.HasServerlessRoute);

        using var http = new HttpClient(new StubHandler("{}"));
        var router = new EllaiReasoningRouter(
            options,
            new EllaiReasoningAdapter(http, options),
            new OllamaEllaiReasoningProvider(http, options),
            new ServerlessEllaiReasoningProvider(http, options));

        Assert.Equal("serverless", router.Resolve().Backend);
    }

    [Fact]
    public void RouterSelectsConfiguredLane()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Ellai_Enabled"] = "true",
            ["Ellai__ReasoningBackend"] = "ollama",
            ["Ellai__Reasoning__Ollama__Endpoint"] = "http://localhost:11434",
            ["Ellai__Reasoning__Ollama__Model"] = "llama3.2:3b",
        });
        using var http = new HttpClient(new StubHandler("{}"));
        var router = new EllaiReasoningRouter(
            options,
            new EllaiReasoningAdapter(http, options),
            new OllamaEllaiReasoningProvider(http, options),
            new ServerlessEllaiReasoningProvider(http, options));

        var selected = router.Resolve();

        Assert.Equal("ollama", selected.Backend);
        Assert.Equal("llama3.2:3b", selected.Model);
    }
}
