using WebRtcVad.NET;

namespace SwiftAid.UI.Ellai;

/// <summary>Client-only WebRTC VAD used to stop local narration promptly.
/// Gateway Silero remains authoritative for turn completion and server state.</summary>
public sealed class EllaiClientBargeInDetector : IDisposable
{
    public const int ConfirmationMilliseconds = 180;
    private const int InputSampleRate = 24_000;
    private const int VadSampleRate = 16_000;
    private const int FrameMilliseconds = 20;
    private const int FrameSamples = VadSampleRate * FrameMilliseconds / 1_000;

    private WebRtcVad.NET.WebRtcVad _vad = CreateVad();
    private readonly short[] _frame = new short[FrameSamples];
    private int _frameCount;
    private int _consecutiveSpeechFrames;
    private bool _speechConfirmed;
    private long _inputSampleIndex;
    private double _nextVadSamplePosition;
    private short _previousInputSample;
    private bool _hasPreviousInputSample;
    private bool _disposed;

    /// <summary>Consumes the existing enhanced 24 kHz mono capture stream.
    /// Returns true once after 180 ms of WebRTC-classified speech.</summary>
    public bool ObservePcm24Khz(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_speechConfirmed)
        {
            return false;
        }
        if ((pcm.Length & 1) != 0)
        {
            throw new ArgumentException("PCM16 input must contain complete samples.", nameof(pcm));
        }

        for (var offset = 0; offset < pcm.Length; offset += sizeof(short))
        {
            var current = (short)(pcm[offset] | pcm[offset + 1] << 8);
            var currentIndex = _inputSampleIndex++;
            if (!_hasPreviousInputSample)
            {
                _previousInputSample = current;
                _hasPreviousInputSample = true;
                continue;
            }

            while (_nextVadSamplePosition < currentIndex)
            {
                var fraction = Math.Clamp(
                    _nextVadSamplePosition - (currentIndex - 1), 0d, 1d);
                var resampled = (short)Math.Clamp(
                    (int)Math.Round(_previousInputSample * (1d - fraction) + current * fraction),
                    short.MinValue,
                    short.MaxValue);
                if (AppendVadSample(resampled))
                {
                    return true;
                }

                _nextVadSamplePosition += (double)InputSampleRate / VadSampleRate;
            }

            _previousInputSample = current;
        }

        return false;
    }

    /// <summary>Resets the detector's adaptive state at the start of a new
    /// assistant response without stopping or resetting microphone capture.</summary>
    public void ResetSpeechConfirmation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _vad.Dispose();
        _vad = CreateVad();
        Array.Clear(_frame);
        _frameCount = 0;
        _consecutiveSpeechFrames = 0;
        _speechConfirmed = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vad.Dispose();
        Array.Clear(_frame);
    }

    private bool AppendVadSample(short sample)
    {
        _frame[_frameCount++] = sample;
        if (_frameCount < FrameSamples)
        {
            return false;
        }

        var speech = _vad.HasSpeech(_frame.AsSpan());
        Array.Clear(_frame);
        _frameCount = 0;
        if (!speech)
        {
            _consecutiveSpeechFrames = 0;
            return false;
        }

        _consecutiveSpeechFrames++;
        if (_consecutiveSpeechFrames * FrameMilliseconds < ConfirmationMilliseconds)
        {
            return false;
        }

        _consecutiveSpeechFrames = 0;
        _speechConfirmed = true;
        return true;
    }

    private static WebRtcVad.NET.WebRtcVad CreateVad() => new()
    {
        SampleRate = SampleRate.Rate16kHz,
        FrameLength = FrameLength.Length20ms,
        OperatingMode = OperatingMode.Aggressive,
    };
}
