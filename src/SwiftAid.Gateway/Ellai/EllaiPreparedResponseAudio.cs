using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Publication starts synthesis. Audio GETs only attach to bounded, replayable PCM.</summary>
public sealed class EllaiPreparedResponseAudio(
    IEllaiSpeechSynthesisClient speech,
    ILogger<EllaiPreparedResponseAudio> logger) : BackgroundService, IEllaiResponseAudioPreparation
{
    private const int Capacity = 64;
    private const int MaximumAudioBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<(string Session, long Sequence), PreparedAudio> _entries = [];
    private readonly Channel<PreparedAudio> _queue = Channel.CreateBounded<PreparedAudio>(Capacity);

    public void Published(EllaiSessionResponseDto response)
    {
        lock (_gate)
        {
            foreach (var pair in _entries.ToArray())
            {
                var prior = pair.Value.Response;
                if (pair.Value.Expires <= DateTimeOffset.UtcNow
                    || (prior.SessionId == response.SessionId
                        && (prior.ClientUtteranceId != response.ClientUtteranceId
                            || (prior.Progress is not null && response.Progress is null)
                            || response.Progress?.State is "Cancelled" or "Interrupted" or "Retrying" or "Failed"
                            || (response.IsReconciliation && response.Outcome != "Completed"))))
                {
                    pair.Value.Cancel();
                    _entries.Remove(pair.Key);
                }
            }

            var key = (response.SessionId, response.Sequence);
            if (_entries.ContainsKey(key) || !speech.IsConfigured || response.Language is not ("eng" or "sna" or "nya" or "tso")
                || string.IsNullOrWhiteSpace(response.Text)
                || (response.Progress is not null && (!response.Progress.Speak
                    || response.Progress.ExpiresAtUtc <= DateTimeOffset.UtcNow))
                || (response.IsReconciliation && response.Outcome != "Completed")) return;

            // Refuse excess work rather than delaying reasoning or growing an unbounded audio cache.
            if (_entries.Count >= Capacity)
            {
                logger.LogWarning("Ellai speech preparation capacity reached; sequence {Sequence} unavailable", response.Sequence);
                return;
            }
            var audio = new PreparedAudio(response, DateTimeOffset.UtcNow + Retention);
            _entries.Add(key, audio);
            if (!_queue.Writer.TryWrite(audio))
            {
                _entries.Remove(key);
                audio.Cancel();
            }
        }
    }

    public void Clear(string sessionId)
    {
        lock (_gate)
        {
            foreach (var pair in _entries.Where(pair => pair.Key.Session == sessionId).ToArray())
            {
                pair.Value.Cancel();
                _entries.Remove(pair.Key);
            }
        }
    }

    public void Interrupt(string sessionId, long sequence)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue((sessionId, sequence), out var audio)) audio.Cancel();
        }
    }

    public async Task<HttpResponseMessage> OpenAsync(string sessionId, long sequence, CancellationToken cancellationToken)
    {
        PreparedAudio audio;
        lock (_gate)
        {
            if (!_entries.TryGetValue((sessionId, sequence), out audio!) || audio.Expires <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Prepared speech is unavailable.");
        }
        var headers = await audio.Headers.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (audio.IsFailed) throw new InvalidOperationException("Prepared speech is unavailable.");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new PreparedAudioReader(audio)),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.swiftaid.pcm");
        foreach (var header in headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return response;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(ProduceAsync(stoppingToken), ProduceAsync(stoppingToken), ExpireAsync(stoppingToken));

    private async Task ExpireAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                lock (_gate)
                {
                    foreach (var pair in _entries.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).ToArray())
                    {
                        pair.Value.Cancel();
                        _entries.Remove(pair.Key);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                foreach (var audio in _entries.Values) audio.Cancel();
                _entries.Clear();
            }
        }
    }

    private async Task ProduceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var audio in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, audio.Cancellation.Token);
                cancellation.CancelAfter(TimeSpan.FromSeconds(90));
                var started = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var sentences = EllaiSpeechSentenceChunker.Split(audio.Response.Text);
                    Dictionary<string, string[]>? format = null;
                    var firstAudio = true;
                    for (var index = 0; index < sentences.Count; index++)
                    {
                        using var upstream = await speech.SynthesizeAsync(
                            $"{audio.Response.SessionId}-{audio.Response.Sequence}-{index}", sentences[index],
                            audio.Response.Language, audio.Response.Language == "eng" ? audio.Response.EnglishVoiceId : null,
                            cancellation.Token);
                        if (!EllaiSpeechResponseContract.IsValidPcmResponse(upstream))
                            throw new InvalidOperationException("Invalid speech response.");
                        var headers = new Dictionary<string, string[]>();
                        foreach (var name in new[] { "X-SwiftAid-Voice-Id", "X-SwiftAid-Sample-Rate", "X-SwiftAid-Channels", "X-SwiftAid-Sample-Format" })
                            if (upstream.Headers.TryGetValues(name, out var values)) headers[name] = values.ToArray();
                        if (format is not null && (format.Count != headers.Count
                            || format.Any(pair => !headers.TryGetValue(pair.Key, out var values) || !pair.Value.SequenceEqual(values))))
                            throw new InvalidOperationException("Speech format changed between sentences.");
                        format ??= headers;
                        audio.Headers.TrySetResult(headers);
                        using var body = await upstream.Content.ReadAsStreamAsync(cancellation.Token);
                        var buffer = new byte[8192];
                        int read;
                        while ((read = await body.ReadAsync(buffer, cancellation.Token)) != 0)
                        {
                            audio.Append(buffer.AsSpan(0, read).ToArray());
                            if (firstAudio)
                            {
                                firstAudio = false;
                                logger.LogInformation("Ellai speech first audio sequence {Sequence} publication-to-audio {ElapsedMs}ms",
                                    audio.Response.Sequence, (DateTimeOffset.UtcNow - audio.Response.CreatedAtUtc).TotalMilliseconds);
                            }
                        }
                    }
                    audio.Complete();
                    logger.LogInformation("Ellai speech prepared sequence {Sequence} in {ElapsedMs}ms", audio.Response.Sequence, started.ElapsedMilliseconds);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // No provider bodies, text, or exception messages enter diagnostics.
                    audio.Fail();
                    logger.LogWarning("Ellai speech preparation ended sequence {Sequence}: {Reason}",
                        audio.Response.Sequence, error is OperationCanceledException ? "cancelled" : "failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private sealed class PreparedAudio(EllaiSessionResponseDto response, DateTimeOffset expires)
    {
        public EllaiSessionResponseDto Response { get; } = response;
        public DateTimeOffset Expires { get; } = expires;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<Dictionary<string, string[]>> Headers { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly List<byte[]> _blocks = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _bytes;
        private bool _complete;
        private bool _failed;
        public bool IsFailed { get { lock (_gate) return _failed; } }

        public void Append(byte[] block)
        {
            lock (_gate)
            {
                if (_failed || _bytes + block.Length > MaximumAudioBytes) throw new InvalidOperationException("Audio preparation limit reached.");
                _blocks.Add(block);
                _bytes += block.Length;
                Pulse();
            }
        }
        public void Complete() { lock (_gate) { _complete = true; Pulse(); } }
        public void Fail()
        {
            lock (_gate)
            {
                _failed = true;
                _blocks.Clear();
                Headers.TrySetCanceled();
                Pulse();
            }
        }
        public void Cancel() { Cancellation.Cancel(); Fail(); }
        private void Pulse() { var previous = _changed; _changed = new(TaskCreationOptions.RunContinuationsAsynchronously); previous.TrySetResult(); }
        public async ValueTask<byte[]?> ReadBlockAsync(int index, CancellationToken token)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_failed) throw new IOException("Prepared speech is unavailable.");
                    if (index < _blocks.Count) return _blocks[index];
                    if (_complete) return null;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(token).ConfigureAwait(false);
            }
        }
    }

    private sealed class PreparedAudioReader(PreparedAudio audio) : Stream
    {
        private int _index;
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;
            var block = await audio.ReadBlockAsync(_index, cancellationToken);
            if (block is null) return 0;
            var length = Math.Min(buffer.Length, block.Length - _offset);
            block.AsMemory(_offset, length).CopyTo(buffer);
            _offset += length;
            if (_offset == block.Length) { _index++; _offset = 0; }
            return length;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
