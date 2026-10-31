using System.Buffers.Binary;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Decodes bounded PCM16 chunks, resamples to 16 kHz, and assembles Silero frames.</summary>
public sealed class SileroVoiceActivityAudioStream : IDisposable
{
    private const int ModelSampleRate = 16_000;
    private const int FrameSampleCount = 512;
    private const int MaximumInputBytes = 64 * 1024;
    private readonly IVoiceActivityDetectionSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<float> _resampleBuffer = new(1024);
    private readonly float[] _frame = new float[FrameSampleCount];
    private int _frameLength;
    private int? _inputSampleRate;
    private double _sourcePosition;
    private long _removedSourceSamples;
    private long _frameStartSourceSample = -1;
    private long _sequence;
    private bool _stateResetPending;
    private bool _disposed;
    private int _disposeStarted;

    public SileroVoiceActivityAudioStream(IVoiceActivityDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _session = detector.CreateSession();
    }

    public async ValueTask<IReadOnlyList<VoiceActivityResult>> AppendPcm16Async(
        ReadOnlyMemory<byte> pcm,
        int sampleRate,
        CancellationToken cancellationToken = default)
    {
        if (pcm.Length > MaximumInputBytes || (pcm.Length & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pcm), "PCM input must be even-sized and no larger than 64 KiB per frame batch.");
        }

        if (sampleRate is < 8_000 or > 96_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sampleRateChanged = _inputSampleRate.HasValue && _inputSampleRate.Value != sampleRate;
            if (sampleRateChanged)
            {
                ResetCore(resetSourceOffset: false);
                _stateResetPending = true;
            }
            _inputSampleRate = sampleRate;

            for (var offset = 0; offset < pcm.Length; offset += sizeof(short))
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Span.Slice(offset, sizeof(short)));
                _resampleBuffer.Add(sample < 0 ? sample / 32768f : sample / 32767f);
            }

            var results = new List<VoiceActivityResult>();
            var ratio = sampleRate / (double)ModelSampleRate;
            while (_sourcePosition + 1 < _resampleBuffer.Count)
            {
                var index = (int)_sourcePosition;
                var fraction = (float)(_sourcePosition - index);
                if (_frameLength == 0)
                {
                    _frameStartSourceSample = _removedSourceSamples + index;
                }
                var sample = _resampleBuffer[index] * (1 - fraction)
                    + _resampleBuffer[index + 1] * fraction;
                _frame[_frameLength++] = sample;
                _sourcePosition += ratio;

                if (_frameLength == FrameSampleCount)
                {
                    var frameSamples = (float[])_frame.Clone();
                    var activity = await _session.AnalyzeAsync(
                        new VoiceActivityFrame(_sequence++, ModelSampleRate, frameSamples), cancellationToken)
                        .ConfigureAwait(false);
                    results.Add(activity with
                    {
                        StateWasReset = activity.StateWasReset || _stateResetPending,
                        AudioStartSample = _frameStartSourceSample,
                        AudioEndSample = _removedSourceSamples + (long)Math.Ceiling(_sourcePosition),
                    });
                    _stateResetPending = false;
                    _frameLength = 0;
                    _frameStartSourceSample = -1;
                }
            }

            var consumed = Math.Min((int)_sourcePosition, Math.Max(0, _resampleBuffer.Count - 1));
            if (consumed > 0)
            {
                _resampleBuffer.RemoveRange(0, consumed);
                _sourcePosition -= consumed;
                _removedSourceSamples += consumed;
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ResetCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetSourceSampleOffset(long sourceSampleOffset)
    {
        if (sourceSampleOffset < 0 || sourceSampleOffset > 16_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSampleOffset));
        }

        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inputSampleRate.HasValue || _sequence != 0 || _frameLength != 0)
            {
                throw new InvalidOperationException("The source offset must be set before audio is appended.");
            }
            _removedSourceSamples = sourceSampleOffset;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.Dispose();
            _resampleBuffer.Clear();
            Array.Clear(_frame);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void ResetCore(bool resetSourceOffset = true)
    {
        _session.Reset();
        _resampleBuffer.Clear();
        Array.Clear(_frame);
        _frameLength = 0;
        _sourcePosition = 0;
        if (resetSourceOffset)
        {
            _removedSourceSamples = 0;
        }
        _frameStartSourceSample = -1;
        _sequence = 0;
        _inputSampleRate = null;
        _stateResetPending = true;
    }
}
