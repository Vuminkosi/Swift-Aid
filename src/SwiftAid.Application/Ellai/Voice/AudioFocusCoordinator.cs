namespace SwiftAid.Application.Ellai;

/// <summary>Audio channel owners competing for capture/playback.</summary>
public enum AudioChannel
{
    EllaiCapture,
    EllaiNarration,
    CaseVoiceNote,
    EvaluationRecording
}

/// <summary>Single-owner audio focus coordinator (ELL-T12). One channel owns
/// capture/playback at a time; Ellai never transcribes its own TTS; mute and
/// microphone-stop are independent of session cancel.</summary>
public sealed class AudioFocusCoordinator
{
    private readonly object _gate = new();
    private AudioChannel? _owner;
    private bool _outputMuted;

    public bool TryAcquire(AudioChannel channel)
    {
        lock (_gate)
        {
            if (_owner is not null && _owner != channel)
            {
                return false;
            }

            _owner = channel;
            return true;
        }
    }

    public void Release(AudioChannel channel)
    {
        lock (_gate)
        {
            if (_owner == channel)
            {
                _owner = null;
            }
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            _outputMuted = muted;
        }
    }

    public bool CanPlayNarration()
    {
        lock (_gate)
        {
            return !_outputMuted && (_owner is null || _owner == AudioChannel.EllaiNarration);
        }
    }

    /// <summary>Self-echo guard: provider audio frames tagged as TTS output
    /// must never enter the capture pipeline as new instructions.</summary>
    public static bool IsSelfEcho(bool frameTaggedAsTtsOutput) => frameTaggedAsTtsOutput;
}
