using System.Net;
using System.Text;
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

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
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

        var result = await provider.CompleteAsync(new[] { ("user", "Reply with the single word: ok") }, 64, CancellationToken.None);

        Assert.Equal("ollama", provider.Backend);
        Assert.Equal("llama3.2:3b", result.Model);
        Assert.Contains("ok", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(12, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task OllamaProviderFailsClosedWhenUnconfigured()
    {
        var options = Bind(new Dictionary<string, string?> { ["Ellai_Enabled"] = "true" });
        using var http = new HttpClient(new StubHandler("{}"));
        var provider = new OllamaEllaiReasoningProvider(http, options);

        await Assert.ThrowsAsync<MetaConfigurationException>(() =>
            provider.CompleteAsync(new[] { ("user", "hi") }, 64, CancellationToken.None));
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
            provider.CompleteAsync(new[] { ("user", "hi") }, 64, CancellationToken.None));
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
