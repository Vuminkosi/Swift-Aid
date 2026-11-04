using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Intelligence.Meta;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiVoiceTranscriptionServiceTests
{
    [Fact]
    public Task RecordedVoiceFlowsThroughFrameworkWorkerToPreparedSpeech() => RunRecordedFrameworkVoiceAsync(false);

    private sealed class LiveSpeechFactAttribute : FactAttribute
    {
        public LiveSpeechFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWIFTAID_TEST_LIVE_SPEECH") != "1")
                Skip = "Requires an explicitly configured local speech fixture.";
        }
    }

    [LiveSpeechFact]
    public Task RecordedVoiceFrameworkResponseUsesLiveSpeech() => RunRecordedFrameworkVoiceAsync(true);

    [LiveSpeechFact]
    public async Task ValidationPromptProducesInterruptibleLiveSpeech()
    {
        var endpoint = Environment.GetEnvironmentVariable("SWIFTAID_TEST_SPEECH_ENDPOINT");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new InvalidOperationException("Live speech fixture requires a loopback endpoint.");
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EllaiTts:Endpoint"] = endpoint,
            ["EllaiTts:InternalKey"] = Environment.GetEnvironmentVariable("SWIFTAID_TEST_SPEECH_KEY")
        }).Build();
        using var http = new HttpClient();
        using var prepared = new EllaiPreparedResponseAudio(new EllaiSpeechSynthesisClient(http, settings),
            NullLogger<EllaiPreparedResponseAudio>.Instance);
        await prepared.StartAsync(default);
        try
        {
            var responses = new EllaiSessionResponseStore(audioPreparation: prepared);
            var text = EllaiValidationPrompts.Resolve("date-required")!;
            var response = responses.PublishProgress("validation-fixture", "synthetic-turn", text,
                new("validation-fixture-event", "Guidance", "Guidance", true, 1, DateTimeOffset.UtcNow.AddMinutes(1)), "af_heart")!;
            Assert.True(responses.CanSpeakProgress(response));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var pcm = await prepared.OpenAsync(response.SessionId, response.Sequence, timeout.Token);
            var bytes = await pcm.Content.ReadAsByteArrayAsync(timeout.Token);
            Assert.True(bytes.Length > 100);
            Assert.Contains(bytes, value => value != 0);
            Assert.Equal("af_heart", Assert.Single(pcm.Headers.GetValues("X-SwiftAid-Voice-Id")));
            responses.InterruptProgress(response.SessionId, response.Sequence);
            Assert.False(responses.CanSpeakProgress(response));
        }
        finally { await prepared.StopAsync(default); }
    }

    private sealed class LiveMuseSpeechFactAttribute : FactAttribute
    {
        public LiveMuseSpeechFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWIFTAID_TEST_LIVE_SPEECH") != "1" ||
                Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1")
                Skip = "Requires configured local speech and live Muse fixtures.";
        }
    }

    [LiveMuseSpeechFact]
    public Task RecordedVoiceUsesLiveMuseAndSpeech() => RunRecordedFrameworkVoiceAsync(true, liveModel: true);

    private sealed class LiveVoicePipelineFactAttribute : FactAttribute
    {
        public LiveVoicePipelineFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWIFTAID_TEST_LIVE_ASR") != "1" ||
                Environment.GetEnvironmentVariable("SWIFTAID_TEST_LIVE_SPEECH") != "1" ||
                Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1")
                Skip = "Requires explicitly configured live transcription, Muse and local speech fixtures.";
        }
    }

    [LiveVoicePipelineFact]
    public Task SyntheticSpeechFlowsThroughLiveTranscriptionMuseAndSpeech() =>
        RunRecordedFrameworkVoiceAsync(true, liveModel: true, liveAsr: true);

    private static async Task RunRecordedFrameworkVoiceAsync(bool liveSpeech, bool liveModel = false, bool liveAsr = false)
    {
        const string sessionId = "framework-recorded-voice";
        var options = liveAsr ? EllaiOptions.Bind(Environment.GetEnvironmentVariable) :
            new EllaiOptions { Enabled = true, ApiKey = "synthetic", AsrModel = "fixture",
                AsrTranscriptionUrl = "https://transcription.test/v1/transcribe" };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "voice-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(new(sessionId, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
            "voice-owner", "ellai-help-identity-epoch-1", null, null, DateTimeOffset.UtcNow), default);
        var consent = new EllaiConsentService();
        var fixtureSpeech = new FixtureSpeech();
        using var speechHttp = new HttpClient();
        var endpoint = Environment.GetEnvironmentVariable("SWIFTAID_TEST_SPEECH_ENDPOINT");
        if (liveSpeech && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback))
            throw new InvalidOperationException("Live speech fixture requires a loopback endpoint.");
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EllaiTts:Endpoint"] = endpoint,
            ["EllaiTts:InternalKey"] = Environment.GetEnvironmentVariable("SWIFTAID_TEST_SPEECH_KEY")
        }).Build();
        IEllaiSpeechSynthesisClient speech = liveSpeech ? new EllaiSpeechSynthesisClient(speechHttp, settings) : fixtureSpeech;
        using var prepared = new EllaiPreparedResponseAudio(speech, NullLogger<EllaiPreparedResponseAudio>.Instance);
        await prepared.StartAsync(default);
        var responses = new EllaiSessionResponseStore(audioPreparation: prepared);
        using var http = liveAsr ? new HttpClient { Timeout = TimeSpan.FromSeconds(90) } : new HttpClient(new StubAsrHandler());
        using var model = new VoiceFrameworkClient();
        var registrations = new ServiceCollection();
        registrations.AddSingleton(consent).AddSingleton<IEllaiSessionStore>(store).AddSingleton<IEllaiUtteranceOutboxStore>(store);
        registrations.AddScoped<EllaiUtteranceSubmissionService>().AddSingleton(new MetaAsrUploadAdapter(http, options));
        registrations.AddSingleton(responses).AddSingleton<EllaiUsageLedger>().AddSingleton<IEllaiUsageStore, InMemoryEllaiUsageStore>();
        registrations.AddScoped(_ => new EllaiPromptBuilder("Synthetic policy", "fixture"));
        registrations.AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), consent));
        var modelOptions = liveModel ? EllaiOptions.Bind(Environment.GetEnvironmentVariable) : new EllaiOptions
        {
            Enabled = true, ApiKey = "synthetic", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture"
        };
        registrations.AddEllaiReasoningExecutor(modelOptions, "framework");
        if (!liveModel) registrations.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey, model);
        await using var provider = registrations.BuildServiceProvider();
        var voice = new EllaiVoiceTranscriptionService(consent, new InMemoryEllaiConsentStore(), options, provider);
        await voice.GrantVoiceConsentAsync(sessionId, default);
        byte[] wave;
        if (liveAsr)
        {
            using var generated = await speech.SynthesizeAsync("synthetic-input", "Hello, what can SwiftAid do?", "eng", "af_heart", default);
            generated.EnsureSuccessStatusCode();
            Assert.Equal("s16le", Assert.Single(generated.Headers.GetValues("X-SwiftAid-Sample-Format")));
            var sampleRate = int.Parse(Assert.Single(generated.Headers.GetValues("X-SwiftAid-Sample-Rate")), System.Globalization.CultureInfo.InvariantCulture);
            var inputPcm = await generated.Content.ReadAsByteArrayAsync();
            Assert.True(inputPcm.Length > 100);
            using var output = new MemoryStream();
            using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("RIFF"u8);
                writer.Write(36 + inputPcm.Length);
                writer.Write("WAVE"u8);
                writer.Write("fmt "u8);
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write("data"u8);
                writer.Write(inputPcm.Length);
                writer.Write(inputPcm);
            }
            wave = output.ToArray();
        }
        else
        {
            wave = new byte[44];
            "RIFF"u8.CopyTo(wave);
            "WAVE"u8.CopyTo(wave.AsSpan(8));
        }
        using var worker = new EllaiUtteranceWorker(provider, NullLogger<EllaiUtteranceWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var audio = new MemoryStream(wave);
            var result = await voice.TranscribeAsync(sessionId, EllaiVoiceCaptureMode.Recorded, audio, audio.Length,
                default, "fixture-capture", submissionPrincipal: principal);
            Assert.Equal(EllaiClientOperationStatus.Accepted, result!.Admission!.Status);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(liveSpeech ? 90 : 10));
            EllaiConversationTurn turn;
            do
            {
                await Task.Delay(25, timeout.Token);
                turn = Assert.Single(await store.ReadConversationAsync(sessionId, "voice-owner", timeout.Token));
            } while (turn.CompletedAtUtc is null);
            Assert.Equal("Completed", turn.Outcome);
            if (liveAsr) Assert.Contains("hello", turn.UserText, StringComparison.OrdinalIgnoreCase);
            else Assert.Equal("recognized fallback words", turn.UserText);
            if (!liveModel) Assert.Contains("recognized fallback words", model.Messages);
            EllaiSessionResponseDto? chunk;
            do
            {
                chunk = responses.ReadSince(sessionId, 0).Responses.FirstOrDefault(
                    item => !item.IsFinal && item.Progress is null && !string.IsNullOrWhiteSpace(item.Text));
                if (chunk is null) await Task.Delay(25, timeout.Token);
            } while (chunk is null);
            using var pcm = await prepared.OpenAsync(sessionId, chunk.Sequence, timeout.Token);
            var bytes = await pcm.Content.ReadAsByteArrayAsync(timeout.Token);
            if (liveSpeech)
            {
                Assert.True(bytes.Length > 100);
                Assert.Contains(bytes, value => value != 0);
                Assert.Equal("af_heart", Assert.Single(pcm.Headers.GetValues("X-SwiftAid-Voice-Id")));
            }
            else
            {
                Assert.Equal(new byte[] { 0, 0, 0, 0 }, bytes);
                Assert.Equal("Synthetic voice answer.", fixtureSpeech.Text);
            }
        }
        finally
        {
            await worker.StopAsync(default);
            await prepared.StopAsync(default);
        }
    }

    private sealed class VoiceFrameworkClient : IChatClient
    {
        public IReadOnlyList<string> Messages { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Messages = messages.Select(item => item.Text).ToArray();
            yield return new ChatResponseUpdate(ChatRole.Assistant, """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Synthetic voice answer.","spokenText":"Synthetic voice answer.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FixtureSpeech : IEllaiSpeechSynthesisClient
    {
        public bool IsConfigured => true;
        public string? Text { get; private set; }
        public Task<HttpResponseMessage> SynthesizeAsync(string requestId, string text, string language, string? voiceId,
            CancellationToken cancellationToken)
        {
            Assert.Equal("eng", language);
            Assert.Equal("af_heart", voiceId);
            return SynthesizeAsync(requestId, text, cancellationToken);
        }
        public Task<HttpResponseMessage> SynthesizeAsync(string requestId, string text, CancellationToken cancellationToken)
        {
            Text = text;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0, 0, 0]) };
            response.Content.Headers.ContentType = new("application/vnd.swiftaid.pcm");
            response.Headers.Add("X-SwiftAid-Voice-Id", "swiftaid-mms-english-v1");
            response.Headers.Add("X-SwiftAid-Sample-Rate", "16000");
            return Task.FromResult(response);
        }
    }
    [Theory]
    [InlineData("voice-owner", EllaiClientOperationStatus.Accepted)]
    [InlineData("other-owner", EllaiClientOperationStatus.Unauthorized)]
    public async Task RecordedVoiceIsAdmittedBeforeReturningAndRetriesKeepOneDurableTurn(
        string actorId, EllaiClientOperationStatus expectedStatus)
    {
        const string sessionId = "recorded-admission";
        var options = new EllaiOptions { Enabled = true, ApiKey = "synthetic",
            AsrModel = "muse-voice-transcribe-1.0", AsrTranscriptionUrl = "https://transcription.test/v1/transcribe" };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, actorId), new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(new(sessionId, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
            "voice-owner", "ellai-help-identity-epoch-1", null, null, DateTimeOffset.UtcNow), CancellationToken.None);
        var consent = new EllaiConsentService();
        using var http = new HttpClient(new StubAsrHandler());
        using var provider = new ServiceCollection().AddSingleton(consent)
            .AddSingleton<IEllaiSessionStore>(store).AddScoped<EllaiUtteranceSubmissionService>()
            .AddSingleton(new MetaAsrUploadAdapter(http, options)).BuildServiceProvider();
        var service = new EllaiVoiceTranscriptionService(consent, new InMemoryEllaiConsentStore(), options, provider);
        await service.GrantVoiceConsentAsync(sessionId, CancellationToken.None);
        // The provider fixture only checks the container signature; no real audio leaves this test.
        var wave = new byte[44];
        "RIFF"u8.CopyTo(wave);
        "WAVE"u8.CopyTo(wave.AsSpan(8));
        string? acceptedId = null;
        for (var retry = 0; retry < 2; retry++)
        {
            using var audio = new MemoryStream(wave);
            var result = await service.TranscribeAsync(sessionId, EllaiVoiceCaptureMode.Recorded,
                audio, audio.Length, CancellationToken.None, "same-capture", submissionPrincipal: principal);
            Assert.NotNull(result);
            Assert.True(result.ServerSubmission);
            Assert.Equal(expectedStatus, result.Admission?.Status);
            if (acceptedId is not null) Assert.Equal(acceptedId, result.ClientUtteranceId);
            acceptedId = result.ClientUtteranceId;
            var conversation = await store.ReadConversationAsync(sessionId, "voice-owner", CancellationToken.None);
            if (expectedStatus == EllaiClientOperationStatus.Accepted)
            {
                Assert.Equal(acceptedId, Assert.Single(conversation).ClientUtteranceId);
                Assert.Equal("Queued", conversation[0].Outcome);
            }
            else Assert.Empty(conversation);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(48_000)]
    public async Task ConfirmedVoiceTurnIsAdmittedWithoutClientPollingOrMicrophoneEof(int finalAfterPcmBytes)
    {
        await using var server = await AudioForwardingWebSocketServer.CreateAsync(finalAfterPcmBytes: finalAfterPcmBytes);
        var options = new EllaiOptions
        {
            Enabled = true, ApiKey = "synthetic-key", AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = server.WebSocketUrl, AsrMode = "DIARIZATION",
        };
        const string sessionId = "gateway-submission";
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "voice-owner"),
            new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(new(sessionId, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
            "voice-owner", "ellai-help-identity-epoch-1", null, null, DateTimeOffset.UtcNow), CancellationToken.None);
        var turns = new EllaiVoiceTurnCoordinator();
        var consent = new EllaiConsentService();
        using var provider = new ServiceCollection().AddSingleton(turns).AddSingleton(consent)
            .AddSingleton<IEllaiSessionStore>(store).AddScoped<EllaiUtteranceSubmissionService>()
            .AddSingleton(new MetaAsrRealtimeAdapter(options))
            .AddSingleton<IVoiceActivityDetector, SpeechThenSilenceDetector>()
            .AddTransient<SileroVoiceActivityAudioStream>().BuildServiceProvider();
        var service = new EllaiVoiceTranscriptionService(consent, new InMemoryEllaiConsentStore(), options, provider);
        await service.GrantVoiceConsentAsync(sessionId, CancellationToken.None);
        await using var audio = new ProgressivePcmStream();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var transcription = service.TranscribeAsync(sessionId, EllaiVoiceCaptureMode.Live, audio, null,
            lifetime.Token, "capture-direct", submissionPrincipal: principal);
        try
        {
            await audio.WriteAsync(new byte[48_000]);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            IReadOnlyList<EllaiConversationTurn> conversation = [];
            while (DateTimeOffset.UtcNow < deadline)
            {
                conversation = await store.ReadConversationAsync(sessionId, "voice-owner", CancellationToken.None);
                if (conversation.Count > 0) break;
                await Task.Delay(20);
            }
            Assert.False(transcription.IsCompleted);
            var accepted = Assert.Single(conversation);
            Assert.Equal("streamed words", accepted.UserText);
            Assert.Equal("Queued", accepted.Outcome);
            Assert.Equal(Assert.Single(turns.GetCompletedTurns(sessionId)).TurnId.ToString("N"), accepted.ClientUtteranceId);
        }
        finally
        {
            audio.Complete();
            await transcription;
        }
    }

    [Fact]
    public async Task SegmentsFinalizeIndependentProviderRequestsWithoutClosingCaptureOrWaitingForClient()
    {
        await using var server = await AudioForwardingWebSocketServer.CreateAsync(); // Final only after endStream.
        var options = new EllaiOptions { Enabled = true, ApiKey = "synthetic", AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = server.WebSocketUrl, AsrMode = "DIARIZATION" };
        const string sessionId = "segmented-admission";
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "voice-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test"));
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(new(sessionId, EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
            "voice-owner", "ellai-help-identity-epoch-1", null, null, DateTimeOffset.UtcNow), CancellationToken.None);
        var turns = new EllaiVoiceTurnCoordinator();
        var consent = new EllaiConsentService();
        using var provider = new ServiceCollection().AddSingleton(turns).AddSingleton(consent)
            .AddSingleton<IEllaiSessionStore>(store).AddScoped<EllaiUtteranceSubmissionService>()
            .AddSingleton(new MetaAsrRealtimeAdapter(options)).AddSingleton<IVoiceActivityDetector, SpeechThenSilenceDetector>()
            .BuildServiceProvider();
        var service = new EllaiVoiceTranscriptionService(consent, new InMemoryEllaiConsentStore(), options, provider);
        await service.GrantVoiceConsentAsync(sessionId, CancellationToken.None);
        var events = new List<EllaiSpeechEvent>();
        long sequence = 0;
        for (var segment = 0; segment < 2; segment++)
        {
            var start = segment * 240_000L;
            events.Add(new(EllaiSpeechEventKind.SpeechStarted, ++sequence, start, 1));
            for (var frame = 0; frame < 40; frame++) events.Add(new(EllaiSpeechEventKind.Audio, ++sequence, start + frame * 768, 1));
            events.Add(new(EllaiSpeechEventKind.SpeechEnded, ++sequence, start + 40 * 768, 1));
        }
        using var socket = new EllaiSegmentedAudioTests.PacketSocket(events, holdOpen: true);
        await using var audio = new EllaiVoiceWebSocketStream(socket);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running = service.TranscribeAsync(sessionId, EllaiVoiceCaptureMode.Live, audio, null, deadline.Token,
            "capture-segments", submissionPrincipal: principal);
        try
        {
            IReadOnlyList<EllaiConversationTurn> conversation = [];
            var inspectUntil = DateTimeOffset.UtcNow.AddSeconds(3);
            while (DateTimeOffset.UtcNow < inspectUntil)
            {
                conversation = await store.ReadConversationAsync(sessionId, "voice-owner", deadline.Token);
                if (conversation.Count == 2 || running.IsCompleted) break;
                await Task.Delay(20, deadline.Token);
            }
            Assert.False(running.IsCompleted);
            Assert.True(conversation.Count == 2, $"durable={conversation.Count}; completed={turns.GetCompletedTurns(sessionId).Count}; state={turns.GetSnapshot(sessionId).State}");
            Assert.All(conversation, turn => Assert.Equal("Queued", turn.Outcome));
            Assert.All(turns.GetCompletedTurns(sessionId), turn => Assert.Equal(1, turn.StreamEpoch));
            Assert.Equal(2, turns.GetCompletedTurns(sessionId).Select(turn => turn.ProviderTurnId).Distinct().Count());
        }
        finally { socket.Complete(); await running; }
    }

    [Fact]
    public async Task ProviderReceiverFailureDoesNotWaitForMicrophoneEof()
    {
        await using var server = await AudioForwardingWebSocketServer.CreateAsync(failAfterHandshake: true);
        var adapter = new MetaAsrRealtimeAdapter(new EllaiOptions
        {
            Enabled = true, ApiKey = "synthetic-key", AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = server.WebSocketUrl, AsrMode = "DIARIZATION",
        });
        await using var microphone = new ProgressivePcmStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.TranscribePcmAsync(microphone, cancellation.Token));
        Assert.Contains("ASR", error.Message);
        Assert.False(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task LiveGatewayReportsProviderFailureWithoutDrainingAnOpenMicrophone()
    {
        await using var server = await AudioForwardingWebSocketServer.CreateAsync(failAfterHandshake: true);
        var options = new EllaiOptions
        {
            Enabled = true, ApiKey = "synthetic-key", AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = server.WebSocketUrl, AsrMode = "DIARIZATION",
        };
        using var provider = new ServiceCollection().AddSingleton(new EllaiVoiceTurnCoordinator())
            .AddSingleton(new MetaAsrRealtimeAdapter(options)).BuildServiceProvider();
        var service = new EllaiVoiceTranscriptionService(new EllaiConsentService(), new InMemoryEllaiConsentStore(), options, provider);
        await service.GrantVoiceConsentAsync("receiver-failure", CancellationToken.None);
        await using var microphone = new ProgressivePcmStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var result = await service.TranscribeAsync("receiver-failure", EllaiVoiceCaptureMode.Live,
            microphone, null, cancellation.Token);
        Assert.Null(result);
        Assert.False(cancellation.IsCancellationRequested);
    }
    [Fact]
    public async Task VoiceConsent_GrantsMicrophoneAndTranscriptionSeparatelyForOneHour()
    {
        var consent = new EllaiConsentService();
        var store = new InMemoryEllaiConsentStore();
        var service = CreateService(consent, store);

        Assert.True(await service.GrantVoiceConsentAsync("session-1", CancellationToken.None));

        var grants = await store.ListBySessionAsync("session-1", CancellationToken.None);
        Assert.Equal(
            new[] { EllaiConsentDimension.Microphone, EllaiConsentDimension.Transcription },
            grants.Select(grant => grant.Dimension).Order().ToArray());
        Assert.All(grants, grant =>
        {
            Assert.Equal(EllaiConsentPurposes.VoiceTranscription, grant.Purpose);
            Assert.Equal(EllaiVoiceTranscriptionService.AudioDataClass, grant.DataClass);
            Assert.InRange(grant.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddHours(1));
        });
        Assert.True(await service.HasConsentAsync("session-1", CancellationToken.None));
        Assert.False(await service.HasConsentAsync("other-session", CancellationToken.None));
    }

    [Fact]
    public async Task Transcription_DoesNotDispatchWithoutBothConsentGrantsOrConfiguredProvider()
    {
        var consent = new EllaiConsentService();
        var store = new InMemoryEllaiConsentStore();
        var service = CreateService(consent, store);
        await using var audio = new MemoryStream(new byte[256]);

        Assert.Null(await service.TranscribeAsync(
            "session-1", EllaiVoiceCaptureMode.Live, audio, audio.Length, CancellationToken.None));

        Assert.True(await service.GrantVoiceConsentAsync("session-1", CancellationToken.None));
        audio.Position = 0;
        Assert.Null(await service.TranscribeAsync(
            "session-1", EllaiVoiceCaptureMode.Live, audio, audio.Length, CancellationToken.None));
    }

    [Fact]
    public async Task LiveTranscriptionFailsClosedWithoutTheAuthoritativeTurnCoordinator()
    {
        var options = new EllaiOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = "wss://transcription.test/asr/realtime",
            AsrMode = "DIARIZATION",
        };
        var consent = new EllaiConsentService();
        var consentStore = new InMemoryEllaiConsentStore();
        var service = new EllaiVoiceTranscriptionService(
            consent, consentStore, options, new ServiceCollection().BuildServiceProvider());
        await service.GrantVoiceConsentAsync("session-no-coordinator", CancellationToken.None);
        await using var audio = new MemoryStream(new byte[24_000]);

        var result = await service.TranscribeAsync(
            "session-no-coordinator", EllaiVoiceCaptureMode.Live, audio, audio.Length, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, audio.Position);
    }

    [Fact]
    public async Task LiveTranscription_FallsBackToConfiguredUploadWhenRealtimeSocketFails()
    {
        var options = new EllaiOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = "wss://127.0.0.1:1/asr/realtime",
            AsrTranscriptionUrl = "https://transcription.test/v1/transcribe",
            AsrMode = "DIARIZATION",
        };
        var handler = new StubAsrHandler();
        var providerServices = new ServiceCollection();
        var turnCoordinator = new EllaiVoiceTurnCoordinator();
        providerServices.AddSingleton(turnCoordinator);
        providerServices.AddSingleton(new MetaAsrRealtimeAdapter(options));
        providerServices.AddSingleton(new MetaAsrUploadAdapter(new HttpClient(handler), options));
        using var provider = providerServices.BuildServiceProvider();
        var consent = new EllaiConsentService();
        var consentStore = new InMemoryEllaiConsentStore();
        var service = new EllaiVoiceTranscriptionService(consent, consentStore, options, provider);
        await service.GrantVoiceConsentAsync("session-fallback", CancellationToken.None);
        await using var audio = new MemoryStream(new byte[4_800]);

        var result = await service.TranscribeAsync(
            "session-fallback", EllaiVoiceCaptureMode.Live, audio, audio.Length, CancellationToken.None);

        Assert.Null(result);
        Assert.True(handler.ReceivedWaveAudio);
        Assert.Equal(EllaiVoiceTurnState.Recovering,
            turnCoordinator.GetSnapshot("session-fallback").State);
        Assert.Empty(turnCoordinator.GetCompletedTurns("session-fallback"));
    }

    [Fact]
    public async Task LiveTranscriptionForwardsAudioBeforeClientCompletesCapture()
    {
        await using var server = await AudioForwardingWebSocketServer.CreateAsync();
        var options = new EllaiOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = server.WebSocketUrl,
            AsrMode = "DIARIZATION",
        };
        var providerServices = new ServiceCollection();
        var turnCoordinator = new EllaiVoiceTurnCoordinator();
        providerServices.AddSingleton(turnCoordinator);
        providerServices.AddSingleton(new MetaAsrRealtimeAdapter(options));
        providerServices.AddSingleton<IVoiceActivityDetector>(_ => new SileroOnnxVoiceActivityDetector());
        providerServices.AddTransient<SileroVoiceActivityAudioStream>();
        using var provider = providerServices.BuildServiceProvider();
        var consent = new EllaiConsentService();
        var consentStore = new InMemoryEllaiConsentStore();
        var service = new EllaiVoiceTranscriptionService(consent, consentStore, options, provider);
        await service.GrantVoiceConsentAsync("session-stream", CancellationToken.None);
        await using var audio = new ProgressivePcmStream();
        var pcm = new byte[24_000 * sizeof(short)];
        var expectedPcm = pcm.ToArray();
        var transcription = service.TranscribeAsync(
            "session-stream", EllaiVoiceCaptureMode.Live, audio, contentLength: null,
            cancellationToken: CancellationToken.None);

        try
        {
            await audio.WriteAsync(pcm);
            await server.FirstAudioReceived.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(transcription.IsCompleted);

            audio.Complete();
            var result = await transcription.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Null(result);
            Assert.Equal(expectedPcm, server.ReceivedPcm);
            Assert.Equal(EllaiVoiceTurnState.Recovering,
                turnCoordinator.GetSnapshot("session-stream").State);
            Assert.Empty(turnCoordinator.GetCompletedTurns("session-stream"));
        }
        finally
        {
            audio.Complete();
        }
    }

    [Fact]
    public async Task LiveTranscriptionReturnsOnlyMuseFinalsConfirmedByGatewayWithinTheSameCapture()
    {
        await using var server = await AudioForwardingWebSocketServer.CreateAsync();
        var options = new EllaiOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = server.WebSocketUrl,
            AsrMode = "DIARIZATION",
        };
        const string sessionId = "session-confirmed-final";
        const string captureId = "capture-confirmed-final";
        var turns = new EllaiVoiceTurnCoordinator();
        var providerServices = new ServiceCollection();
        providerServices.AddSingleton(turns);
        providerServices.AddSingleton(new MetaAsrRealtimeAdapter(options));
        providerServices.AddSingleton<IVoiceActivityDetector, SpeechThenSilenceDetector>();
        providerServices.AddTransient<SileroVoiceActivityAudioStream>();
        using var provider = providerServices.BuildServiceProvider();
        var service = new EllaiVoiceTranscriptionService(
            new EllaiConsentService(), new InMemoryEllaiConsentStore(), options, provider);
        await service.GrantVoiceConsentAsync(sessionId, CancellationToken.None);
        await using var audio = new MemoryStream(new byte[24_000 * sizeof(short)]);

        var result = await service.TranscribeAsync(
            sessionId, EllaiVoiceCaptureMode.Live, audio, audio.Length, CancellationToken.None, captureId);

        Assert.Equal("streamed words", result?.Transcript);
        var completed = Assert.Single(turns.GetCompletedTurns(sessionId));
        Assert.Equal("streamed words", completed.Transcript);
        Assert.Equal(captureId, completed.VoiceCaptureId);
        Assert.Equal(turns.GetSnapshot(sessionId).Epoch, completed.StreamEpoch);
    }

    [Fact]
    public async Task LiveTranscriptionWithoutProviderFinalLeavesTurnRecovering()
    {
        await using var server = await AudioForwardingWebSocketServer.CreateAsync(sendFinalTranscript: false);
        var options = new EllaiOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = server.WebSocketUrl,
            AsrMode = "DIARIZATION",
        };
        var turns = new EllaiVoiceTurnCoordinator();
        var providerServices = new ServiceCollection();
        providerServices.AddSingleton(turns);
        providerServices.AddSingleton(new MetaAsrRealtimeAdapter(options));
        using var provider = providerServices.BuildServiceProvider();
        var service = new EllaiVoiceTranscriptionService(
            new EllaiConsentService(), new InMemoryEllaiConsentStore(), options, provider);
        await service.GrantVoiceConsentAsync("session-recovery", CancellationToken.None);
        await using var audio = new MemoryStream(new byte[24_000 * sizeof(short)]);

        var result = await service.TranscribeAsync(
            "session-recovery", EllaiVoiceCaptureMode.Live, audio, audio.Length, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(EllaiVoiceTurnState.Recovering, turns.GetSnapshot("session-recovery").State);
    }

    [Fact]
    public async Task LiveTranscriptionDurationLimitCancelsProviderAndLeavesTurnRecovering()
    {
        await using var server = await AudioForwardingWebSocketServer.CreateAsync(sendFinalTranscript: false);
        var options = new EllaiOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = server.WebSocketUrl,
            AsrMode = "DIARIZATION",
        };
        var turns = new EllaiVoiceTurnCoordinator();
        var providerServices = new ServiceCollection();
        providerServices.AddSingleton(turns);
        providerServices.AddSingleton(new EllaiVoiceTurnOptions(MaximumTurnDurationMs: 750));
        providerServices.AddSingleton(new MetaAsrRealtimeAdapter(options));
        using var provider = providerServices.BuildServiceProvider();
        var service = new EllaiVoiceTranscriptionService(
            new EllaiConsentService(), new InMemoryEllaiConsentStore(), options, provider);
        await service.GrantVoiceConsentAsync("session-turn-timeout", CancellationToken.None);
        await using var audio = new ProgressivePcmStream();
        var transcription = service.TranscribeAsync(
            "session-turn-timeout", EllaiVoiceCaptureMode.Live, audio,
            contentLength: null, cancellationToken: CancellationToken.None);

        try
        {
            await audio.WriteAsync(new byte[24_000 * sizeof(short)]);
            await server.FirstAudioReceived.WaitAsync(TimeSpan.FromSeconds(10));

            var result = await transcription.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Null(result);
            Assert.Equal(EllaiVoiceTurnState.Recovering,
                turns.GetSnapshot("session-turn-timeout").State);
        }
        finally
        {
            audio.Complete();
        }
    }

    [Fact]
    public async Task LiveProviderPartialIsVisibleDuringCaptureButFinalRemainsAuthoritative()
    {
        await using var server = await AudioForwardingWebSocketServer.CreateAsync(
            sendFinalTranscript: true, sendPartialTranscript: true);
        var options = new EllaiOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = server.WebSocketUrl,
            AsrMode = "DIARIZATION",
        };
        var turns = new EllaiVoiceTurnCoordinator();
        var providerServices = new ServiceCollection();
        providerServices.AddSingleton(turns);
        providerServices.AddSingleton(new MetaAsrRealtimeAdapter(options));
        using var provider = providerServices.BuildServiceProvider();
        var service = new EllaiVoiceTranscriptionService(
            new EllaiConsentService(), new InMemoryEllaiConsentStore(), options, provider);
        await service.GrantVoiceConsentAsync("session-partial", CancellationToken.None);
        await using var audio = new ProgressivePcmStream();
        var transcription = service.TranscribeAsync(
            "session-partial", EllaiVoiceCaptureMode.Live, audio, contentLength: null,
            cancellationToken: CancellationToken.None);

        try
        {
            await audio.WriteAsync(new byte[24_000 * sizeof(short)]);
            await server.FirstAudioReceived.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForPartialTranscriptAsync(turns, "session-partial", TimeSpan.FromSeconds(5));
            Assert.Equal("currently speaking", turns.GetSnapshot("session-partial").PartialTranscript);

            audio.Complete();
            var result = await transcription.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Null(result);
            Assert.Null(turns.GetSnapshot("session-partial").PartialTranscript);
        }
        finally
        {
            audio.Complete();
        }
    }

    private static async Task WaitForPartialTranscriptAsync(
        EllaiVoiceTurnCoordinator turns, string sessionId, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (turns.GetSnapshot(sessionId).PartialTranscript is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
        }
    }

    [Fact]
    public async Task LiveTranscriptionVadConfirmsBargeInAndCancelsOnlyTheActiveResponsePlayback()
    {
        await using var server = await AudioForwardingWebSocketServer.CreateAsync();
        var options = new EllaiOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            AsrModel = "muse-voice-transcribe-1.0",
            AsrWebSocketUrl = server.WebSocketUrl,
            AsrMode = "DIARIZATION",
        };
        const string sessionId = "session-barge-in";
        const long responseSequence = 6;
        var playbackId = Guid.NewGuid();
        var turns = new EllaiVoiceTurnCoordinator();
        var playbackCoordinator = new EllaiResponseSpeechCoordinator();
        using var playback = playbackCoordinator.Begin(
            sessionId, responseSequence, playbackId, CancellationToken.None);
        turns.SetAssistantPlayback(sessionId, responseSequence, playbackId, isPlaying: true);

        var providerServices = new ServiceCollection();
        providerServices.AddSingleton(turns);
        providerServices.AddSingleton(playbackCoordinator);
        providerServices.AddSingleton(new MetaAsrRealtimeAdapter(options));
        providerServices.AddSingleton<IVoiceActivityDetector, ContinuousSpeechDetector>();
        providerServices.AddTransient<SileroVoiceActivityAudioStream>();
        using var provider = providerServices.BuildServiceProvider();
        var consent = new EllaiConsentService();
        var consentStore = new InMemoryEllaiConsentStore();
        var service = new EllaiVoiceTranscriptionService(consent, consentStore, options, provider);
        await service.GrantVoiceConsentAsync(sessionId, CancellationToken.None);
        await using var audio = new MemoryStream(new byte[24_000 * sizeof(short)]);

        var result = await service.TranscribeAsync(
            sessionId, EllaiVoiceCaptureMode.Live, audio, audio.Length, CancellationToken.None);

        Assert.Null(result);
        Assert.True(playback.Token.IsCancellationRequested);
        Assert.Equal(EllaiVoiceTurnState.Recovering, turns.GetSnapshot(sessionId).State);
        Assert.Empty(turns.GetCompletedTurns(sessionId));
    }

    private static EllaiVoiceTranscriptionService CreateService(
        EllaiConsentService consent, InMemoryEllaiConsentStore store)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        return new EllaiVoiceTranscriptionService(consent, store, new EllaiOptions(), provider);
    }

    private sealed class StubAsrHandler : HttpMessageHandler
    {
        public bool ReceivedWaveAudio { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            ReceivedWaveAudio = Encoding.Latin1.GetString(body).Contains("RIFF", StringComparison.Ordinal)
                && Encoding.Latin1.GetString(body).Contains("ellai-live-fallback.wav", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"sessionId\":\"asr-session\",\"transcript\":\"recognized fallback words\",\"audioDurationMs\":100,\"turns\":[]}",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ContinuousSpeechDetector : IVoiceActivityDetector
    {
        public IVoiceActivityDetectionSession CreateSession() => new Session();

        private sealed class Session : IVoiceActivityDetectionSession
        {
            private long _sequence;

            public ValueTask<VoiceActivityResult> AnalyzeAsync(
                VoiceActivityFrame frame, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(new VoiceActivityResult(_sequence++, 0.95f, true, false));

            public void Reset() => _sequence = 0;
            public void Dispose() { }
        }
    }

    private sealed class ProgressivePcmStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public ValueTask WriteAsync(byte[] chunk) => _chunks.Writer.WriteAsync(chunk);
        public void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                if (_current is not null)
                {
                    Array.Clear(_current);
                    _current = null;
                    _offset = 0;
                }

                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                    || !_chunks.Reader.TryRead(out _current))
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class AudioForwardingWebSocketServer : IAsyncDisposable
    {
        private readonly bool _sendFinalTranscript;
        private readonly bool _sendPartialTranscript;
        private readonly bool _failAfterHandshake;
        private readonly int _finalAfterPcmBytes;
        private WebApplication _application = null!;
        private readonly TaskCompletionSource _firstAudioReceived = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string WebSocketUrl { get; private set; } = string.Empty;
        public Task FirstAudioReceived => _firstAudioReceived.Task;
        public byte[] ReceivedPcm { get; private set; } = [];

        private AudioForwardingWebSocketServer(bool sendFinalTranscript, bool sendPartialTranscript, bool failAfterHandshake, int finalAfterPcmBytes)
        {
            _sendFinalTranscript = sendFinalTranscript;
            _sendPartialTranscript = sendPartialTranscript;
            _failAfterHandshake = failAfterHandshake;
            _finalAfterPcmBytes = finalAfterPcmBytes;
        }

        public static async Task<AudioForwardingWebSocketServer> CreateAsync(
            bool sendFinalTranscript = true, bool sendPartialTranscript = false, bool failAfterHandshake = false,
            int finalAfterPcmBytes = 0)
        {
            var result = new AudioForwardingWebSocketServer(sendFinalTranscript, sendPartialTranscript, failAfterHandshake, finalAfterPcmBytes);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            result._application = builder.Build();
            result._application.UseWebSockets();
            result._application.Run(result.ServeAsync);
            await result._application.StartAsync().ConfigureAwait(false);

            var address = result._application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var uri = new Uri(address);
            result.WebSocketUrl = $"ws://{uri.Host}:{uri.Port}/asr/realtime";
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync().ConfigureAwait(false);
            await _application.DisposeAsync().ConfigureAwait(false);
        }

        private async Task ServeAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            _ = await ReceiveTextAsync(socket).ConfigureAwait(false);
            await SendTextAsync(socket, "{\"sessionId\":\"stream-session\"}").ConfigureAwait(false);
            if (_failAfterHandshake)
            {
                await SendTextAsync(socket, "{\"type\":\"error\",\"message\":\"synthetic receiver failure\"}").ConfigureAwait(false);
                return;
            }

            using var pcm = new MemoryStream();
            var buffer = new byte[4096];
            var partialSent = false;
            var finalSent = false;
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                if (received.MessageType == WebSocketMessageType.Binary)
                {
                    pcm.Write(buffer, 0, received.Count);
                    _firstAudioReceived.TrySetResult();
                    if (_sendFinalTranscript && !finalSent && _finalAfterPcmBytes > 0 && pcm.Length >= _finalAfterPcmBytes)
                    {
                        finalSent = true;
                        await SendTextAsync(socket,
                            "{\"type\":\"transcript\",\"final\":true,\"turnId\":1,\"transcript\":\"streamed words\"}")
                            .ConfigureAwait(false);
                    }
                    if (_sendPartialTranscript && !partialSent)
                    {
                        partialSent = true;
                        await SendTextAsync(socket,
                            "{\"type\":\"transcript\",\"final\":false,\"turnId\":1,\"transcript\":\" currently speaking \"}")
                            .ConfigureAwait(false);
                    }
                    continue;
                }
                if (Encoding.UTF8.GetString(buffer, 0, received.Count).Contains("endStream", StringComparison.Ordinal))
                {
                    break;
                }
            }

            ReceivedPcm = pcm.ToArray();
            if (_sendFinalTranscript && !finalSent)
            {
                await SendTextAsync(socket,
                    "{\"type\":\"transcript\",\"final\":true,\"turnId\":1,\"transcript\":\"streamed words\"}")
                    .ConfigureAwait(false);
            }
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", CancellationToken.None)
                .ConfigureAwait(false);
        }

        private static async Task<string> ReceiveTextAsync(WebSocket socket)
        {
            using var message = new MemoryStream();
            var buffer = new byte[4096];
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                message.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);
            return Encoding.UTF8.GetString(message.ToArray());
        }

        private static Task SendTextAsync(WebSocket socket, string text) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private sealed class SpeechThenSilenceDetector : IVoiceActivityDetector
    {
        public IVoiceActivityDetectionSession CreateSession() => new Session();

        private sealed class Session : IVoiceActivityDetectionSession
        {
            private long _sequence;

            public ValueTask<VoiceActivityResult> AnalyzeAsync(
                VoiceActivityFrame frame, CancellationToken cancellationToken = default)
            {
                var sequence = _sequence++;
                return ValueTask.FromResult(new VoiceActivityResult(
                    frame.Sequence, sequence < 4 ? 0.95f : 0f, sequence < 4, false));
            }

            public void Reset() => _sequence = 0;
            public void Dispose() { }
        }
    }
}
