using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiMuseChatClientTests
{
    [Fact]
    public async Task OfficialAdapterDoesNotAssembleSplitFunctionName()
    {
        using var handler = new FragmentedToolHandler(splitName: true);
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.Create(new EllaiOptions
        {
            Enabled = true, ApiUrl = "https://fixture.invalid/v1", ApiKey = "fixture-key", ReasoningModel = "fixture-model"
        }, http);
        var names = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "time?")]))
            names.AddRange(update.Contents.OfType<FunctionCallContent>().Select(call => call.Name));
        Assert.Equal(new[] { "ellai_" }, names);
    }

    [Fact]
    public async Task FrameworkRejectsSplitFunctionNameThatOfficialAdapterCannotAssemble()
    {
        using var handler = new FragmentedToolHandler(splitName: true);
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.Create(new EllaiOptions
        {
            Enabled = true, ApiUrl = "https://fixture.invalid/v1", ApiKey = "fixture-key", ReasoningModel = "fixture-model"
        }, http);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.Now });
        var invoked = false;
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(context, "policy",
            [new ChatMessage(ChatRole.User, "time?")], 100, _ => Task.FromResult(true), (_, _, _) =>
            {
                invoked = true;
                throw new InvalidOperationException();
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (_, _) => ValueTask.CompletedTask));
        Assert.Equal(EllaiToolReasonCodes.UnregisteredTool, error.Code);
        Assert.False(invoked);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task OfficialAdapterAssemblesFragmentedStreamingToolCallsForFramework()
    {
        using var handler = new FragmentedToolHandler();
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.Create(new EllaiOptions
        {
            Enabled = true, ApiUrl = "https://fixture.invalid/v1", ApiKey = "fixture-key", ReasoningModel = "fixture-model"
        }, http);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.Now });
        var calls = new List<EllaiToolRequest>();
        var chunks = new List<string>();
        var answer = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "time?")], 200,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                calls.Add(request);
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "Synthetic time", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; });

        Assert.Equal(2, handler.Requests);
        var call = Assert.Single(calls);
        Assert.Equal(EllaiToolNames.Now, call.ToolName);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("Done.", answer?.DisplayText);
        Assert.Equal("Done.", string.Concat(chunks));
    }

    [Fact]
    public async Task StructuredFrameworkSendsDecisionSchemaWithNativeToolsOnEveryCall()
    {
        using var handler = new FragmentedToolHandler();
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.Create(new EllaiOptions
        {
            Enabled = true, ApiUrl = "https://fixture.invalid/v1", ApiKey = "fixture-key", ReasoningModel = "fixture-model"
        }, http);
        var agent = new EllaiFrameworkAgent(client, new EllaiStructuredFrameworkAgentFactory());
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.Now });
        var answer = await agent.RunAsync(context, "Synthetic policy", [new ChatMessage(ChatRole.User, "time?")], 200,
            _ => Task.FromResult(true), (_, request, _) => Task.FromResult(
                new EllaiToolResult(request.ToolName, true, null, [], "Synthetic time", 1, false)),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (_, _) => ValueTask.CompletedTask);

        Assert.Equal("Done.", answer?.DisplayText);
        Assert.Equal(2, handler.Bodies.Count);
        foreach (var body in handler.Bodies)
        {
            using var document = JsonDocument.Parse(body);
            var format = document.RootElement.GetProperty("response_format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            var schema = format.GetProperty("json_schema").GetProperty("schema");
            Assert.True(schema.GetProperty("properties").TryGetProperty("final_decision", out _));
            Assert.True(schema.GetProperty("properties").TryGetProperty("stream_segments", out _));
            Assert.Contains(schema.GetProperty("required").EnumerateArray(), item => item.GetString() == "stream_segments");
            Assert.Contains("null", schema.GetProperty("properties").GetProperty("stream_segments").GetRawText(), StringComparison.Ordinal);
        }
        using var first = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("ellai_now", first.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    private sealed class FragmentedToolHandler(bool splitName = false) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var payload = Requests == 1
                ? """
                  data: {"id":"fixture","object":"chat.completion.chunk","created":1,"model":"fixture-model","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"ellai_now","arguments":"{"}}]},"finish_reason":null}]}

                  data: {"id":"fixture","object":"chat.completion.chunk","created":1,"model":"fixture-model","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"}"}}]},"finish_reason":null}]}

                  data: {"id":"fixture","object":"chat.completion.chunk","created":1,"model":"fixture-model","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

                  data: [DONE]

                  """
                : "data: " + JsonSerializer.Serialize(new
                {
                    id = "fixture", @object = "chat.completion.chunk", created = 1, model = "fixture-model",
                    choices = new[] { new { index = 0, delta = new { role = "assistant", content =
                        """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""" }, finish_reason = "stop" } }
                }) + "\n\ndata: [DONE]\n\n";
            if (Requests == 1 && splitName)
                payload = payload.Replace("\"name\":\"ellai_now\"", "\"name\":\"ellai_\"", StringComparison.Ordinal)
                    .Replace("\"function\":{\"arguments\":\"}\"}", "\"function\":{\"name\":\"now\",\"arguments\":\"}\"}", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
            };
        }
    }

    [Fact]
    public async Task OfficialClientSendsConfiguredRouteAndParsesNativeToolCall()
    {
        var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.Create(new EllaiOptions
        {
            Enabled = true, ApiUrl = "https://fixture.invalid/v1", ApiKey = "fixture-key", ReasoningModel = "fixture-model"
        }, http);
        var tool = AIFunctionFactory.Create((string value) => value, "lookup");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")],
            new ChatOptions { Tools = [tool], MaxOutputTokens = 123 });

        Assert.Equal("https://fixture.invalid/v1/chat/completions", handler.RequestUri);
        Assert.Equal("Bearer fixture-key", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("fixture-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(123, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("lookup", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("lookup", call.Name);
    }

    [Fact]
    public async Task OllamaAdapterUsesConfiguredServerWithoutAuthorizationAndStreams()
    {
        using var handler = new OllamaStreamingHandler();
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.CreateOllama(new EllaiOptions
        {
            Enabled = true, ReasoningBackend = "ollama", OllamaEndpoint = "http://127.0.0.1:11434", OllamaModel = "fixture-model"
        }, http);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")],
            new ChatOptions { MaxOutputTokens = 123 }))
            updates.Add(update);
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", handler.RequestUri);
        Assert.Null(handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("fixture-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(123, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Contains("Hello", string.Concat(updates.Select(update => update.Text)));
    }

    private sealed class OllamaStreamingHandler : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                data: {"id":"fixture","object":"chat.completion.chunk","created":1,"model":"fixture-model","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}]}

                data: {"id":"fixture","object":"chat.completion.chunk","created":1,"model":"fixture-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """, Encoding.UTF8, "text/event-stream") };
        }
    }

    [Fact]
    public async Task OllamaCompatibleStreamRunsFrameworkToolAndFinalDecision()
    {
        using var handler = new FragmentedToolHandler();
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.CreateOllama(new EllaiOptions
        {
            Enabled = true, ReasoningBackend = "ollama", OllamaEndpoint = "http://127.0.0.1:11434", OllamaModel = "fixture-model"
        }, http);
        var agent = new EllaiFrameworkAgent(client);
        var calls = new List<EllaiToolRequest>();
        var result = await agent.RunAsync(new EllaiToolContext("session", "epoch", "cases", null, 4, true,
                AllowedTools: new HashSet<string> { EllaiToolNames.Now }), "policy", [new ChatMessage(ChatRole.User, "time?")], 200,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                calls.Add(request);
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "Synthetic time", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (_, _) => ValueTask.CompletedTask);
        Assert.Equal("Done.", result?.DisplayText);
        Assert.Equal(EllaiToolNames.Now, Assert.Single(calls).ToolName);
        Assert.Equal(2, handler.Requests);
        using var followUp = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Contains(followUp.RootElement.GetProperty("messages").EnumerateArray(), message =>
            message.GetProperty("role").GetString() == "tool" &&
            message.GetProperty("tool_call_id").GetString() == "call-1");
    }

    [Fact]
    public async Task ServerlessAdapterUsesConfiguredRouteAndNonStreamingToolResponse()
    {
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.CreateServerless(new EllaiOptions
        {
            Enabled = true, ReasoningBackend = "serverless", ServerlessEnabled = true,
            ServerlessEndpoint = "https://fixture.invalid/v1", ServerlessApiKey = "serverless-key",
            ServerlessModel = "serverless-model"
        }, http);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")],
            new ChatOptions { Tools = [AIFunctionFactory.Create((string value) => value, "lookup")], MaxOutputTokens = 123 }))
            updates.Add(update);
        Assert.Equal("https://fixture.invalid/v1/chat/completions", handler.RequestUri);
        Assert.Equal("Bearer serverless-key", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("serverless-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(123, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean());
        Assert.Equal("lookup", Assert.Single(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>()).Name);
    }

    [Fact]
    public async Task ServerlessAdapterRunsFrameworkToolAndFinalDecision()
    {
        using var handler = new ServerlessAgentHandler();
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.CreateServerless(new EllaiOptions
        {
            Enabled = true, ReasoningBackend = "serverless", ServerlessEnabled = true,
            ServerlessEndpoint = "https://fixture.invalid/v1", ServerlessApiKey = "key", ServerlessModel = "fixture-model"
        }, http);
        var agent = new EllaiFrameworkAgent(client);
        var context = new EllaiToolContext("session", "epoch", "cases", null, 4, true,
            AllowedTools: new HashSet<string> { EllaiToolNames.Now });
        var calls = new List<EllaiToolRequest>();
        var result = await agent.RunAsync(context, "policy", [new ChatMessage(ChatRole.User, "time?")], 200,
            _ => Task.FromResult(true), (_, request, _) =>
            {
                calls.Add(request);
                return Task.FromResult(new EllaiToolResult(request.ToolName, true, null, [], "Synthetic time", 1, false));
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (_, _) => ValueTask.CompletedTask);
        Assert.Equal("Done.", result?.DisplayText);
        Assert.Equal(EllaiToolNames.Now, Assert.Single(calls).ToolName);
        Assert.Equal(2, handler.Requests);
        using var followUp = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Contains(followUp.RootElement.GetProperty("messages").EnumerateArray(), message =>
            message.GetProperty("role").GetString() == "tool" &&
            message.GetProperty("tool_call_id").GetString() == "call-1");
    }

    private sealed class ServerlessAgentHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var message = Requests == 1
                ? "\"content\":null,\"tool_calls\":[{\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"ellai_now\",\"arguments\":\"{}\"}}]"
                : "\"content\":" + JsonSerializer.Serialize("""
                    {"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Done.","spokenText":"Done.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}
                    """);
            var finish = Requests == 1 ? "tool_calls" : "stop";
            var body = "{\"id\":\"fixture\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"," + message + "},\"finish_reason\":\"" + finish + "\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoredProviderRouteRejectsToolOutsideAllowlist(bool ollama)
    {
        using var handler = new ServerlessAgentHandler();
        using var http = new HttpClient(handler);
        var options = ollama
            ? new EllaiOptions { Enabled = true, ReasoningBackend = "ollama",
                OllamaEndpoint = "http://127.0.0.1:11434", OllamaModel = "fixture-model" }
            : new EllaiOptions { Enabled = true, ReasoningBackend = "serverless", ServerlessEnabled = true,
                ServerlessEndpoint = "https://fixture.invalid/v1", ServerlessApiKey = "key", ServerlessModel = "fixture-model" };
        using var client = ollama ? EllaiMuseChatClientFactory.CreateOllama(options, http)
            : EllaiMuseChatClientFactory.CreateServerless(options, http);
        var agent = new EllaiFrameworkAgent(client);
        var invoked = false;
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(
            new EllaiToolContext("session", "epoch", "cases", null, 4, true,
                AllowedTools: new HashSet<string>()), "policy", [new ChatMessage(ChatRole.User, "time?")], 100,
            _ => Task.FromResult(true), (_, _, _) =>
            {
                invoked = true;
                throw new InvalidOperationException("Unexpected tool invocation");
            }, new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default));
        Assert.Equal(EllaiToolReasonCodes.UnregisteredTool, error.Code);
        Assert.False(invoked);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ServerlessAuthenticationFailureIsSanitizedByFramework()
    {
        using var handler = new ServerlessFailureHandler();
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.CreateServerless(new EllaiOptions
        {
            Enabled = true, ReasoningBackend = "serverless", ServerlessEnabled = true,
            ServerlessEndpoint = "https://fixture.invalid/v1", ServerlessApiKey = "private-key", ServerlessModel = "fixture-model"
        }, http);
        var agent = new EllaiFrameworkAgent(client);
        var chunks = new List<string>();
        var error = await Assert.ThrowsAsync<EllaiFrameworkFailureException>(() => agent.RunAsync(
            new EllaiToolContext("session", "epoch", "cases", null, 4, true), "policy",
            [new ChatMessage(ChatRole.User, "hello")], 100, _ => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException("Unexpected tool invocation"),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), default,
            (chunk, _) => { chunks.Add(chunk); return ValueTask.CompletedTask; }));
        Assert.Equal("ProviderAuthenticationFailed", error.Code);
        Assert.DoesNotContain("private-provider-detail", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ServerlessCancellationReachesTransportAndPublishesNoAnswer()
    {
        using var handler = new HeldServerlessHandler();
        using var http = new HttpClient(handler);
        using var client = EllaiMuseChatClientFactory.CreateServerless(new EllaiOptions
        {
            Enabled = true, ReasoningBackend = "serverless", ServerlessEnabled = true,
            ServerlessEndpoint = "https://fixture.invalid/v1", ServerlessApiKey = "key", ServerlessModel = "fixture-model"
        }, http);
        using var cancellation = new CancellationTokenSource();
        var chunks = new List<string>();
        var agent = new EllaiFrameworkAgent(client);
        var run = agent.RunAsync(new EllaiToolContext("session", "epoch", "cases", null, 4, true), "policy",
            [new ChatMessage(ChatRole.User, "hello")], 100, _ => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException("Unexpected tool invocation"),
            new EllaiFrameworkUsage("session", new EllaiUsageLedger(), new InMemoryEllaiUsageStore()), cancellation.Token,
            (chunk, _) => { chunks.Add(chunk); return ValueTask.CompletedTask; });
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(chunks);
    }

    private sealed class ServerlessFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent("private-provider-detail", Encoding.UTF8, "text/plain") });
    }

    private sealed class HeldServerlessHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unexpected transport completion");
        }
    }

    [Fact]
    public void UnconfiguredAndNonMetaRoutesFailClosed()
    {
        Assert.Throws<MetaConfigurationException>(() => EllaiMuseChatClientFactory.Create(new EllaiOptions()));
        Assert.Throws<MetaConfigurationException>(() => EllaiMuseChatClientFactory.Create(new EllaiOptions
        {
            Enabled = true, ApiUrl = "https://fixture.invalid/v1", ApiKey = "fixture-key",
            ReasoningModel = "fixture-model", ReasoningBackend = "ollama"
        }));
        Assert.Throws<MetaConfigurationException>(() => EllaiMuseChatClientFactory.CreateServerless(new EllaiOptions
        {
            Enabled = true, ReasoningBackend = "serverless", ServerlessEnabled = false,
            ServerlessEndpoint = "https://fixture.invalid/v1", ServerlessApiKey = "key", ServerlessModel = "model"
        }));
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public string RequestUri { get; private set; } = string.Empty;
        public string Authorization { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            const string response = """
                {"id":"fixture","object":"chat.completion","created":1,"model":"fixture-model","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"value\":\"example\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
