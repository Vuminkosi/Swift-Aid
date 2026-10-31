namespace SwiftAid.Gateway.Ellai;

/// <summary>Feeds safe sentence-sized response pieces to MMS-TTS in order and
/// exposes their PCM bodies as one continuous response stream.</summary>
internal sealed class EllaiSentencePcmStream : Stream
{
    private readonly IEllaiSpeechSynthesisClient _speech;
    private readonly IReadOnlyList<string> _sentences;
    private readonly string _requestPrefix;
    private readonly CancellationToken _cancellationToken;
    private HttpResponseMessage? _currentResponse;
    private Stream? _currentAudio;
    private int _nextSentenceIndex = 1;
    private bool _disposed;

    public EllaiSentencePcmStream(
        IEllaiSpeechSynthesisClient speech,
        IReadOnlyList<string> sentences,
        HttpResponseMessage firstResponse,
        string requestPrefix,
        CancellationToken cancellationToken)
    {
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _sentences = sentences ?? throw new ArgumentNullException(nameof(sentences));
        _currentResponse = firstResponse ?? throw new ArgumentNullException(nameof(firstResponse));
        _requestPrefix = requestPrefix ?? throw new ArgumentNullException(nameof(requestPrefix));
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
        {
            return 0;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellationToken, cancellationToken);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            _currentAudio ??= await _currentResponse!.Content
                .ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var read = await _currentAudio.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            if (read > 0)
            {
                return read;
            }

            await DisposeCurrentAsync().ConfigureAwait(false);
            if (_nextSentenceIndex >= _sentences.Count)
            {
                return 0;
            }

            var index = _nextSentenceIndex++;
            var response = await _speech.SynthesizeAsync(
                $"{_requestPrefix}-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                _sentences[index], linked.Token).ConfigureAwait(false);
            if (!EllaiSpeechResponseContract.IsValidPcmResponse(response))
            {
                response.Dispose();
                throw new InvalidOperationException("MMS-TTS returned an invalid PCM sentence stream.");
            }

            _currentResponse = response;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _currentAudio?.Dispose();
            _currentResponse?.Dispose();
            _currentAudio = null;
            _currentResponse = null;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_currentAudio is not null)
        {
            await _currentAudio.DisposeAsync().ConfigureAwait(false);
            _currentAudio = null;
        }
        _currentResponse?.Dispose();
        _currentResponse = null;
        GC.SuppressFinalize(this);
    }

    private async ValueTask DisposeCurrentAsync()
    {
        if (_currentAudio is not null)
        {
            await _currentAudio.DisposeAsync().ConfigureAwait(false);
            _currentAudio = null;
        }
        _currentResponse?.Dispose();
        _currentResponse = null;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class EllaiSpeechSentenceChunker
{
    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var sentences = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('.' or '?' or '!'))
            {
                continue;
            }

            var next = index + 1;
            if (next < text.Length && !char.IsWhiteSpace(text[next]))
            {
                continue;
            }

            Add(text.AsSpan(start, next - start), sentences);
            while (next < text.Length && char.IsWhiteSpace(text[next]))
            {
                next++;
            }
            start = next;
            index = next - 1;
        }

        Add(text.AsSpan(start), sentences);
        return sentences;
    }

    private static void Add(ReadOnlySpan<char> value, ICollection<string> sentences)
    {
        var sentence = value.Trim();
        if (!sentence.IsEmpty)
        {
            sentences.Add(sentence.ToString());
        }
    }
}
