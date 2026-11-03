using SwiftAid.Intelligence.Meta;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using SwiftAid.Application.Ellai;

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
