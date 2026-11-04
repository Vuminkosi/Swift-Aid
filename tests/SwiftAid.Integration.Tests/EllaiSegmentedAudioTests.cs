using System.Net.WebSockets;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Mobile.Platforms.Android;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiSegmentedAudioTests
{
    [Fact]
    public async Task IntentionalGapPreservesPositionsWithoutInventingPcm()
    {
        using var socket = new PacketSocket([
            new(EllaiSpeechEventKind.Heartbeat, 1, 1_440_000, 1),
            new(EllaiSpeechEventKind.SpeechStarted, 2, 1_420_800, 1),
            new(EllaiSpeechEventKind.Audio, 3, 1_420_800, 1),
            new(EllaiSpeechEventKind.SpeechEnded, 4, 1_421_568, 1)]);
        await using var stream = new EllaiVoiceWebSocketStream(socket);
        var pcm = new byte[1536];
        Assert.Equal(1536, await stream.ReadAsync(pcm));
        Assert.Equal(1_420_800, stream.LastReadSamplePosition);
        Assert.Equal(1536, stream.ReceivedBytes);
        Assert.Equal(0, await stream.ReadAsync(pcm));
        Assert.Equal(1_439_232, stream.SuppressedSamples);
    }

    [Theory]
    [InlineData(3, 1, 768)] // Missing sequence.
    [InlineData(2, 2, 768)] // Changed epoch.
    [InlineData(2, 1, 0)] // Repeated audio with a new event ID.
    public async Task CorruptOrderingRequiresRecovery(long sequence, long epoch, long position)
    {
        using var socket = new PacketSocket([
            new(EllaiSpeechEventKind.Audio, 1, 0, 1),
            new(EllaiSpeechEventKind.Audio, sequence, position, epoch)]);
        await using var stream = new EllaiVoiceWebSocketStream(socket);
        var pcm = new byte[1536];
        Assert.Equal(1536, await stream.ReadAsync(pcm));
        await Assert.ThrowsAsync<InvalidDataException>(async () => { var count = await stream.ReadAsync(pcm); Assert.Equal(1536, count); });
    }

    [Fact]
    public async Task DuplicatePacketIsNotForwardedTwice()
    {
        using var socket = new PacketSocket([
            new(EllaiSpeechEventKind.Audio, 1, 0, 1), new(EllaiSpeechEventKind.Audio, 1, 0, 1),
            new(EllaiSpeechEventKind.Audio, 2, 768, 1)]);
        await using var stream = new EllaiVoiceWebSocketStream(socket);
        var pcm = new byte[1536];
        Assert.Equal(1536, await stream.ReadAsync(pcm));
        Assert.Equal(1536, await stream.ReadAsync(pcm));
        Assert.Equal(768, stream.LastReadSamplePosition);
        Assert.Equal(1, stream.DuplicateEvents);
        Assert.Equal(3072, stream.ReceivedBytes);
    }

    [Fact]
    public void ActualNativeSileroModelSuppressesOneMinuteOfDigitalSilence()
    {
        using var detector = new AndroidSileroSpeechDetectorFactory().Create();
        using var gate = new SwiftAid.UI.Ellai.EllaiSilenceGate(1);
        for (var i = 0; i < 1875; i++)
        {
            var pcm = new byte[1536];
            Assert.Empty(gate.Push(pcm, detector.AnalyzeFrame(pcm)));
        }
        Assert.Equal(0, gate.AudioBytes);
        Assert.Equal(1_440_000, gate.Position);
    }

    internal sealed class PacketSocket(IEnumerable<EllaiSpeechEvent> events, bool holdOpen = false) : WebSocket
    {
        private readonly Queue<byte[]> _packets = new(events.Select(e => e.Encode(e.Kind == EllaiSpeechEventKind.Audio ? new byte[1536] : [])));
        private readonly TaskCompletionSource _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete() => _close.TrySetResult();
        private byte[]? _active;
        private int _offset;
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => EllaiSpeechEvent.Protocol;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_active is null && !_packets.TryDequeue(out _active))
            {
                if (holdOpen) await _close.Task.WaitAsync(cancellationToken);
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }
            // Exercise fragmented packets, not just single ReceiveAsync delivery.
            var count = Math.Min(137, Math.Min(buffer.Count, _active!.Length - _offset));
            _active.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;
            var end = _offset == _active.Length;
            if (end) { _active = null; _offset = 0; }
            return new WebSocketReceiveResult(count, WebSocketMessageType.Binary, end);
        }
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
