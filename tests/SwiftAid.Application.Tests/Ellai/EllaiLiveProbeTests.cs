using SwiftAid.Intelligence.Meta;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using SwiftAid.Application.Ellai;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

/// <summary>Live provider probes. Run only with the Ellai section configured
/// in the environment; otherwise each test skips (never a mock pass).
/// Nothing sensitive is asserted into output: only route, model identity,
/// status fields, durations and usage counts are recorded.</summary>
[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T24")]
[Trait("Live", "Provider")]
public sealed class EllaiLiveProbeTests
{
    private sealed record ProbeDecisionEnvelope([property: JsonPropertyName("final_decision")] EllaiDecision FinalDecision);

    private sealed class SchemaProbeFactory : IEllaiFrameworkAgentFactory
    {
        public AIAgent Create(IChatClient client, ChatClientAgentOptions options)
        {
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
                Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
            };
            var schema = JsonSerializer.SerializeToElement(JsonSchemaExporter.GetJsonSchemaAsNode(jsonOptions, typeof(ProbeDecisionEnvelope)));
            options.ChatOptions!.ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "ellai_decision");
            return EllaiFrameworkAgentFactory.Instance.Create(client, options);
        }
    }

    [LiveFact]
    public async Task LiveMuseBuffersEvidenceFreeSegmentsUntilDecisionCompletes()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(LiveOptions(), http);
        var capture = new CaptureStreamingClient(client);
        var context = new EllaiToolContext("live-segment", "epoch", "public.product-help", null, 4, true,
            AllowedTools: new HashSet<string>());
        var segments = new List<string>();
        var agent = new EllaiFrameworkAgent(capture);
        const string instructions = "Answer briefly. Return JSON containing final_decision with schemaVersion 2, kind Answer, " +
            "displayText, spokenText, claims, questions, uiIntents, planProposal, topicRefs, resumeSuggestion and stopReason. " +
            "Use empty arrays and null for unused fields.";
        var result = await agent.RunAsync(context, instructions,
            [new ChatMessage(ChatRole.User, "Say hello and ask how you can help.")], 2048,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException(),
            new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()),
            CancellationToken.None, onTextDelta: (segment, _) =>
            {
                Assert.True(capture.Completed);
                segments.Add(segment);
                return ValueTask.CompletedTask;
            }, onVerifiedStreamSegment: (_, _) => throw new InvalidOperationException("An evidence-free segment was published early."));
        Assert.NotEmpty(segments);
        Assert.Equal(result?.DisplayText, string.Join(" ", segments));
    }

    [LiveFact]
    public async Task LiveFrameworkStreamsFullDecisionSchemaWithBrokeredTool()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(LiveOptions(), http);
        var consent = new EllaiConsentService();
        consent.Grant("live-schema-session", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);
        var context = new EllaiToolContext("live-schema-session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var invoked = 0;
        var chunks = new List<string>();
        var result = await new EllaiFrameworkAgent(client, new SchemaProbeFactory()).RunAsync(context,
            "Use the available tool when requested. Return a final_decision with schemaVersion 2, empty arrays and null for unused fields.",
            [new ChatMessage(ChatRole.User, "Use ellai_now and report the current UTC time briefly.")], 2048,
            _ => Task.FromResult(true), async (toolContext, request, ct) =>
            {
                invoked++;
                return await broker.InvokeAsync(toolContext, request, ct);
            }, new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), new InMemoryEllaiUsageStore()),
            CancellationToken.None, (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; });
        Assert.True(invoked > 0);
        Assert.NotNull(result);
        Assert.Equal(result!.DisplayText, string.Join(" ", chunks));
    }

    [LiveFact]
    public async Task LiveMuseSupportsFullDecisionSchemaWithNativeToolCall()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
        };
        var schema = JsonSerializer.SerializeToElement(JsonSchemaExporter.GetJsonSchemaAsNode(jsonOptions, typeof(ProbeDecisionEnvelope)));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(LiveOptions(), http);
        var tool = AIFunctionFactory.Create((string value) => value, "fixture_echo", "Echo a synthetic value.");
        var options = new ChatOptions
        {
            MaxOutputTokens = 2048, Tools = [tool], ToolMode = ChatToolMode.Auto,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "ellai_decision")
        };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Call fixture_echo with value banana, then report the result in final_decision.displayText. Set schemaVersion to 2.")
        };
        var response = await client.GetResponseAsync(messages, options);
        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("fixture_echo", call.Name);
        messages.AddRange(response.Messages);
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "banana")]));
        var final = await client.GetResponseAsync(messages, options);
        var parsed = EllaiModelTurnParser.Parse(final.Text);
        Assert.NotNull(parsed.Turn?.FinalDecision);
        Assert.Empty(EllaiDecisionValidator.Validate(parsed.Turn!.FinalDecision!));
        Assert.Contains("banana", parsed.Turn.FinalDecision!.DisplayText, StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact]
    public async Task LiveMuseStreamingCancellationStopsFrameworkTurn()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = new CancelAfterLiveUpdateClient(EllaiMuseChatClientFactory.Create(LiveOptions(), http), cancellation);
        var store = new InMemoryEllaiUsageStore();
        var context = new EllaiToolContext("live-cancel-session", "epoch", "public.product-help", null, 4, true,
            AllowedTools: new HashSet<string>());
        var published = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EllaiFrameworkAgent(client).RunAsync(
            context, "Synthetic policy", [new ChatMessage(ChatRole.User, "Say hello briefly.")], 1024,
            _ => Task.FromResult(true), (_, _, _) => throw new InvalidOperationException("No tool is expected."),
            new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), store), cancellation.Token,
            (text, _) => { published.Add(text); return ValueTask.CompletedTask; }));
        Assert.True(client.Updates > 0, "Cancellation must follow an actual live provider update.");
        Assert.Empty(published);
        Assert.Equal(1, (await store.TotalsAsync(context.SessionId, default)).Calls);
    }

    private sealed class CancelAfterLiveUpdateClient(IChatClient inner, CancellationTokenSource cancellation) : DelegatingChatClient(inner)
    {
        public int Updates { get; private set; }
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                Updates++;
                cancellation.Cancel();
                yield return update;
            }
        }
    }

    private static EllaiOptions LiveOptions()
    {
        var options = EllaiOptions.Bind(Environment.GetEnvironmentVariable);
        if (!options.Enabled || options.Validate().Count != 0)
        {
            throw new InvalidOperationException("Ellai live configuration is absent or invalid.");
        }

        return options;
    }

    /// <summary>Runs only when explicitly opted in; otherwise skipped at
    /// discovery so the default suite never mock-passes a live gate.</summary>
    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("ELLAI_LIVE"), "1", StringComparison.Ordinal))
            {
                Skip = "Set ELLAI_LIVE=1 with Ellai configuration present to run live provider probes.";
            }
        }
    }

    [LiveFact]
    public async Task LiveOfficialChatClientReturnsSyntheticText()
    {
        var options = LiveOptions();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(options, http);
        ChatResponse response;
        try
        {
            response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word: ok")],
                new ChatOptions { MaxOutputTokens = 1024 });
        }
        catch (System.ClientModel.ClientResultException error)
        {
            // Never place a provider response body or credential in test output.
            throw new InvalidOperationException($"Provider rejected the official chat client request (HTTP {error.Status}).");
        }

        Assert.Contains("ok", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(options.ReasoningModel, response.ModelId);
        Assert.True(response.Usage?.TotalTokenCount > 0);
    }

    [LiveFact]
    public async Task LiveOfficialChatClientSupportsJsonSchemaWithNativeToolCalls()
    {
        var options = LiveOptions();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(options, http);
        using var schema = System.Text.Json.JsonDocument.Parse("""
            {"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}
            """);
        var tool = AIFunctionFactory.Create((string value) => value, "fixture_echo", "Echo a synthetic value.");
        ChatResponse response;
        try
        {
            response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Call fixture_echo with value banana. Do not answer directly.")],
                new ChatOptions
                {
                    MaxOutputTokens = 1024, Tools = [tool], ToolMode = ChatToolMode.Auto,
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement, "fixture_response")
                });
        }
        catch (System.ClientModel.ClientResultException error)
        {
            throw new InvalidOperationException($"Provider rejected schema plus tool calling (HTTP {error.Status}).");
        }
        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("fixture_echo", call.Name);
        Assert.Contains("banana", call.Arguments!["value"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        var history = new List<ChatMessage> { new(ChatRole.User, "Call fixture_echo with value banana, then report the result in the answer field.") };
        history.AddRange(response.Messages);
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "banana")]));
        try
        {
            var final = await client.GetResponseAsync(history, new ChatOptions
            {
                MaxOutputTokens = 1024, Tools = [tool], ToolMode = ChatToolMode.Auto,
                ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement, "fixture_response")
            });
            using var json = System.Text.Json.JsonDocument.Parse(final.Text);
            Assert.Contains("banana", json.RootElement.GetProperty("answer").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        catch (System.ClientModel.ClientResultException error)
        {
            throw new InvalidOperationException($"Provider rejected schema after the tool result (HTTP {error.Status}).");
        }
    }

    [LiveFact]
    public async Task LiveOfficialChatClientReturnsNativeToolCall()
    {
        var options = LiveOptions();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(options, http);
        var tool = AIFunctionFactory.Create((string value) => value, "fixture_echo", "Echo a synthetic value.");
        ChatResponse response;
        try
        {
            response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Call fixture_echo with value equal to banana. Do not answer directly.")],
                new ChatOptions { MaxOutputTokens = 1024, Tools = [tool], ToolMode = ChatToolMode.Auto });
        }
        catch (System.ClientModel.ClientResultException error)
        {
            throw new InvalidOperationException($"Provider rejected the official tool-call request (HTTP {error.Status}).");
        }

        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("fixture_echo", call.Name);
        Assert.False(string.IsNullOrWhiteSpace(call.CallId));
        Assert.NotNull(call.Arguments);
        Assert.Contains("banana", call.Arguments["value"]?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact]
    public async Task LiveFrameworkAgentInvokesSyntheticTool()
    {
        var options = LiveOptions();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(options, http);
        var invoked = 0;
        var tool = AIFunctionFactory.Create((string value) =>
        {
            Interlocked.Increment(ref invoked);
            return $"The synthetic result is {value}.";
        }, "fixture_echo", "Echo a synthetic value.");
        AIAgent agent = client.AsAIAgent(name: "EllaiSyntheticProbe", tools: [tool]);
        var session = await agent.CreateSessionAsync();
        try
        {
            var response = await agent.RunAsync("Use fixture_echo with value banana, then report its result.", session);
            Assert.Equal(1, invoked);
            Assert.Contains("banana", response.Text, StringComparison.OrdinalIgnoreCase);
        }
        catch (System.ClientModel.ClientResultException error)
        {
            throw new InvalidOperationException($"Provider rejected the framework agent request (HTTP {error.Status}).");
        }
    }

    [LiveFact]
    public async Task LiveFrameworkAgentReasonsAcrossDependentToolCalls()
    {
        var options = LiveOptions();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(options, http);
        var secretCode = "fixture-" + Guid.NewGuid().ToString("N")[..8];
        var firstCalls = 0;
        var acceptedSecondCalls = 0;
        var getCode = AIFunctionFactory.Create(() =>
        {
            firstCalls++;
            return secretCode;
        }, "get_fixture_code", "Get the synthetic code needed by verify_fixture_code.");
        var verifyCode = AIFunctionFactory.Create((string code) =>
        {
            if (code == secretCode) { acceptedSecondCalls++; return "verified"; }
            return "invalid";
        }, "verify_fixture_code", "Verify the exact code returned by get_fixture_code.");
        AIAgent agent = client.AsAIAgent(name: "EllaiMultiTurnProbe", tools: [getCode, verifyCode]);
        var session = await agent.CreateSessionAsync();
        try
        {
            var response = await agent.RunAsync("Get the fixture code, pass it exactly to verify_fixture_code, then report whether it was verified.", session);
            Assert.True(firstCalls > 0);
            Assert.True(acceptedSecondCalls > 0);
            Assert.Contains("verif", response.Text, StringComparison.OrdinalIgnoreCase);
        }
        catch (System.ClientModel.ClientResultException error)
        {
            throw new InvalidOperationException($"Provider rejected the multi-turn probe (HTTP {error.Status}).");
        }
    }

    [LiveFact]
    public async Task LiveFrameworkAgentInvokesBrokeredTool()
    {
        var options = LiveOptions();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(options, http);
        var consent = new EllaiConsentService();
        consent.Grant("synthetic-session", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);
        var context = new EllaiToolContext("synthetic-session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var calls = 0;
        EllaiToolResult? observation = null;
        var tools = EllaiFrameworkToolCatalog.Create(context, async (toolContext, request, ct) =>
        {
            Interlocked.Increment(ref calls);
            observation = await broker.InvokeAsync(toolContext, request, ct);
            return observation;
        });
        AIAgent agent = client.AsAIAgent(name: "EllaiBrokerProbe", tools: tools.ToList());
        var session = await agent.CreateSessionAsync();
        try
        {
            var response = await agent.RunAsync("Use ellai_now to check the current UTC time and report it.", session);
            Assert.True(calls > 0);
            Assert.NotNull(observation);
            Assert.True(observation.Allowed);
            Assert.False(string.IsNullOrWhiteSpace(response.Text));
        }
        catch (System.ClientModel.ClientResultException error)
        {
            throw new InvalidOperationException($"Provider rejected the brokered tool request (HTTP {error.Status}).");
        }
    }

    [LiveFact]
    public Task LiveFrameworkPreservesAuthorizedCapabilityObservationForFollowup() =>
        VerifyCapabilityFollowupAsync(1024);

    [LiveFact]
    public Task LiveFrameworkPreservesCapabilityObservationAtGatewayTokenLimit() =>
        VerifyCapabilityFollowupAsync(2048);

    private static async Task VerifyCapabilityFollowupAsync(int maxTokens)
    {
        var options = LiveOptions();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(options, http);
        var consent = new EllaiConsentService();
        consent.Grant("synthetic-session", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);
        var context = new EllaiToolContext("synthetic-session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Capabilities });
        var actor = SwiftAid.Application.Authorization.ActorContext.FromValidatedPrincipal(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "synthetic-owner"),
                 new System.Security.Claims.Claim(SwiftAid.Application.Authorization.ActorContext.AccessEpochClaimType, "1")], "test")));
        var ledger = new EllaiUsageLedger();
        var usage = new EllaiFrameworkUsage(context.SessionId, ledger, new InMemoryEllaiUsageStore());
        var runner = new EllaiFrameworkAgent(client);
        EllaiObservedToolResult? observation = null;
        const string instructions = "Use the available tool when asked. Return only this JSON shape: " +
            "{\"final_decision\":{\"schemaVersion\":2,\"kind\":\"Answer\",\"displayText\":\"...\",\"spokenText\":\"...\",\"claims\":[],\"questions\":[],\"uiIntents\":[],\"planProposal\":null,\"topicRefs\":[],\"resumeSuggestion\":null,\"stopReason\":null}}";
        var decision = await runner.RunAsync(context, instructions,
            [new ChatMessage(ChatRole.User, "Call ellai_capabilities without arguments and report the result.")], maxTokens,
            _ => Task.FromResult(true), async (toolContext, request, ct) =>
            {
                var result = await broker.InvokeAsync(toolContext, request, ct);
                observation = EllaiObservedToolResult.From(request, result);
                return result;
            }, usage, default);
        Assert.NotNull(decision);
        Assert.Equal(EllaiObservedToolResult.CurrentCapabilityVersion, observation?.CapabilityVersion);
        var trace = EllaiToolTrace.SerializeFlattened([observation!], false, []);
        var turns = new[]
        {
            new EllaiConversationTurn(context.SessionId, "prior", 1, "Check the capability", decision!.DisplayText,
                "Completed", 1, DateTimeOffset.UtcNow) { ToolTraceJson = trace },
            new EllaiConversationTurn(context.SessionId, "current", 2, "What did you just check?", null, "Queued", 0, null)
        };
        var history = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", actor, null, default);
        Assert.Contains(history.Messages, message => message.Content == decision.DisplayText);
        Assert.Contains(history.InheritedObservations, item => item.CapabilityVersion == EllaiObservedToolResult.CurrentCapabilityVersion);
        var messages = history.Messages.Select(message => new ChatMessage(
            message.Role == "user" ? ChatRole.User : ChatRole.Assistant, message.Content)).ToList();
        messages.Add(new ChatMessage(ChatRole.User, "What tool result did you just discuss?"));
        var followup = await runner.RunAsync(context, instructions, messages, maxTokens, _ => Task.FromResult(true),
            (toolContext, request, ct) => broker.InvokeAsync(toolContext, request, ct), usage, default);
        Assert.NotNull(followup);
        Assert.False(string.IsNullOrWhiteSpace(followup!.DisplayText));
    }

    [LiveFact]
    public async Task LiveFrameworkRunnerStreamsBrokeredToolDecision()
    {
        var options = LiveOptions();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(options, http);
        var consent = new EllaiConsentService();
        consent.Grant("synthetic-session", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromMinutes(5));
        var broker = new EllaiToolBroker(new EllaiPolicyService(), consent);
        var context = new EllaiToolContext("synthetic-session", "epoch", "cases", null, 4, true,
            DataClassification: EllaiProcessingClassification.SyntheticDemo,
            AllowedTools: new HashSet<string>(StringComparer.Ordinal) { EllaiToolNames.Now });
        var store = new InMemoryEllaiUsageStore();
        var invoked = 0;
        var streamed = new System.Text.StringBuilder();
        const string instructions = "Use the available tool when asked. Return only this JSON shape: " +
            "{\"final_decision\":{\"schemaVersion\":2,\"kind\":\"Answer\",\"displayText\":\"...\",\"spokenText\":\"...\",\"claims\":[],\"questions\":[],\"uiIntents\":[],\"planProposal\":null,\"topicRefs\":[],\"resumeSuggestion\":null,\"stopReason\":null}}";
        var capture = new CaptureStreamingClient(client);
        var runner = new EllaiFrameworkAgent(capture);
        try
        {
            var result = await runner.RunAsync(context, instructions,
                [new ChatMessage(ChatRole.User, "Use ellai_now to check the current UTC time and report it.")], 1024,
                _ => Task.FromResult(true), async (toolContext, request, ct) =>
                {
                    invoked++;
                    return await broker.InvokeAsync(toolContext, request, ct);
                }, new EllaiFrameworkUsage(context.SessionId, new EllaiUsageLedger(), store), CancellationToken.None,
                (text, _) => { streamed.Append(text); return ValueTask.CompletedTask; });
            Assert.True(invoked > 0);
            var parsed = EllaiModelTurnParser.Parse(capture.Text.ToString());
            var perCall = string.Join(",", capture.Calls.Select(value => $"{value.Length}:{EllaiModelTurnParser.DiagnosticCode(EllaiModelTurnParser.Parse(value.ToString()).Error)}"));
            Assert.True(result is not null, $"Decision was rejected: {EllaiModelTurnParser.DiagnosticCode(parsed.Error)}; parsedDecision={parsed.Turn?.FinalDecision is not null}; validationErrors={(parsed.Turn?.FinalDecision is { } d ? EllaiDecisionValidator.Validate(d).Count : -1)}; calls={perCall}.");
            Assert.Equal(result!.DisplayText, streamed.ToString());
            var totals = await store.TotalsAsync(context.SessionId, CancellationToken.None);
            Assert.True(totals.Calls >= 2);
            Assert.True(totals.TotalTokens > 0);
        }
        catch (System.ClientModel.ClientResultException error)
        {
            throw new InvalidOperationException($"Provider rejected the streamed framework runner request (HTTP {error.Status}).");
        }
    }

    private sealed class CaptureStreamingClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public bool Completed { get; private set; }
        public System.Text.StringBuilder Text { get; } = new();
        public List<System.Text.StringBuilder> Calls { get; } = [];
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var current = new System.Text.StringBuilder();
            Calls.Add(current);
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                Text.Append(update.Text);
                current.Append(update.Text);
                yield return update;
            }
            Completed = true;
        }
    }

    [LiveFact]
    public async Task LiveOfficialChatClientStreamsSyntheticText()
    {
        var options = LiveOptions();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var client = EllaiMuseChatClientFactory.Create(options, http);
        var text = new System.Text.StringBuilder();
        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word: ok")],
                new ChatOptions { MaxOutputTokens = 1024 }))
                text.Append(update.Text);
        }
        catch (System.ClientModel.ClientResultException error)
        {
            throw new InvalidOperationException($"Provider rejected the official streaming request (HTTP {error.Status}).");
        }

        Assert.Contains("ok", text.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact]
    public async Task LiveReasoningReturnsStopWithConfiguredModel()
    {
        var options = LiveOptions();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var adapter = new EllaiReasoningAdapter(http, options!);
        var result = await adapter.CompleteAsync(
            [new SwiftAid.Application.Ellai.EllaiChatMessage("user", "Reply with the single word: ok")],
            1024, CancellationToken.None);

        Assert.Equal("stop", result.FinishReason);
        Assert.Equal(options!.ReasoningModel, result.Model);
        Assert.Contains("ok", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Usage.TotalTokens > 0);
    }

    [LiveFact]
    public async Task LiveAsrUploadTranscribesSyntheticTone()
    {
        var options = LiveOptions();

        using var tone = SyntheticToneWav(16000, 2, 440);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var adapter = new MetaAsrUploadAdapter(http, options!);
        var result = await adapter.TranscribeAsync(tone, "tone.wav", "audio/wav", CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        Assert.True(result.AudioDurationMs >= 1500);
        Assert.NotNull(result.Transcript);
    }

    [LiveFact]
    public async Task LiveRealtimeAsrStreamsSyntheticTone()
    {
        var options = LiveOptions();
        var adapter = new MetaAsrRealtimeAdapter(options);
        using var pcm = SyntheticTonePcm(24000, 2, 440);
        var result = await adapter.TranscribePcmAsync(pcm, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        Assert.NotNull(result.FinalTranscripts);
    }

    private static MemoryStream SyntheticTonePcm(int sampleRate, int seconds, int frequency)
    {
        var stream = new MemoryStream();
        var frames = sampleRate * seconds;
        foreach (var t in Enumerable.Range(0, frames))
        {
            var sample = (short)(12000 * Math.Sin(2 * Math.PI * frequency * t / sampleRate));
            stream.Write(BitConverter.GetBytes(sample));
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream SyntheticToneWav(int sampleRate, int seconds, int frequency)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var frames = sampleRate * seconds;
            var dataSize = frames * 2;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            for (var t = 0; t < frames; t++)
            {
                writer.Write((short)(12000 * Math.Sin(2 * Math.PI * frequency * t / sampleRate)));
            }
        }

        stream.Position = 0;
        return stream;
    }
}
