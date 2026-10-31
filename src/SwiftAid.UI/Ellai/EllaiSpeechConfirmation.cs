namespace SwiftAid.UI.Ellai;

/// <summary>Confirms sustained above-threshold speech activity using captured
/// frame duration, so scheduling delays do not inflate the confirmation time.</summary>
public sealed class EllaiSpeechConfirmation
{
    private readonly double _rmsThreshold;
    private readonly TimeSpan _confirmationDuration;
    private TimeSpan _candidateDuration;
    private bool _signaled;

    public EllaiSpeechConfirmation(double rmsThreshold, TimeSpan confirmationDuration)
    {
        if (!double.IsFinite(rmsThreshold) || rmsThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rmsThreshold));
        }
        if (confirmationDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(confirmationDuration));
        }

        _rmsThreshold = rmsThreshold;
        _confirmationDuration = confirmationDuration;
    }

    public bool Observe(double rms, TimeSpan frameDuration, bool echoCancellationEnabled)
    {
        if (!echoCancellationEnabled || !double.IsFinite(rms) || rms < _rmsThreshold
            || frameDuration <= TimeSpan.Zero)
        {
            Reset();
            return false;
        }

        if (_signaled)
        {
            return false;
        }

        _candidateDuration += frameDuration;
        if (_candidateDuration < _confirmationDuration)
        {
            return false;
        }

        _signaled = true;
        return true;
    }

    public void Reset()
    {
        _candidateDuration = TimeSpan.Zero;
        _signaled = false;
    }
}
