using Microsoft.Extensions.DependencyInjection;
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

namespace SwiftAid.Integration.Tests;

public sealed class EllaiVoiceTranscriptionServiceTests
{
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

        Assert.Equal("recognized fallback words", result?.Transcript);
        Assert.True(handler.ReceivedWaveAudio);
        Assert.Equal(EllaiVoiceTurnState.Processing,
            turnCoordinator.GetSnapshot("session-fallback").State);
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

            Assert.Equal("streamed words", result?.Transcript);
            Assert.Equal(expectedPcm, server.ReceivedPcm);
            Assert.Equal(EllaiVoiceTurnState.Processing,
                turnCoordinator.GetSnapshot("session-stream").State);
        }
        finally
        {
            audio.Complete();
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

        Assert.Equal("streamed words", result?.Transcript);
        Assert.True(playback.Token.IsCancellationRequested);
        Assert.Equal(EllaiVoiceTurnState.Processing, turns.GetSnapshot(sessionId).State);
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
        private WebApplication _application = null!;
        private readonly TaskCompletionSource _firstAudioReceived = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string WebSocketUrl { get; private set; } = string.Empty;
        public Task FirstAudioReceived => _firstAudioReceived.Task;
        public byte[] ReceivedPcm { get; private set; } = [];

        public static async Task<AudioForwardingWebSocketServer> CreateAsync()
        {
            var result = new AudioForwardingWebSocketServer();
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

            using var pcm = new MemoryStream();
            var buffer = new byte[4096];
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
                    continue;
                }
                if (Encoding.UTF8.GetString(buffer, 0, received.Count).Contains("endStream", StringComparison.Ordinal))
                {
                    break;
                }
            }

            ReceivedPcm = pcm.ToArray();
            await SendTextAsync(socket,
                "{\"type\":\"transcript\",\"final\":true,\"turnId\":1,\"transcript\":\"streamed words\"}")
                .ConfigureAwait(false);
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
}
