namespace SwiftAid.UI.Ellai;

/// <summary>Effective audio-processing capabilities for one active capture.
/// A null capability means the platform does not expose an independent status
/// for that feature.</summary>
public sealed record EllaiAudioProcessingStatus(
    string Platform,
    bool EchoCancellationEnabled,
    bool? NoiseSuppressionEnabled,
    bool? AutomaticGainControlEnabled,
    bool CaptureActive)
{
    public static EllaiAudioProcessingStatus Unknown { get; } =
        new("unknown", false, null, null, false);

    public string ToStatusText()
    {
        static string State(bool? enabled) => enabled switch
        {
            true => "on",
            false => "off or unavailable",
            null => "not reported",
        };

        return $"{Platform} audio processing: echo cancellation {State(EchoCancellationEnabled)}, "
            + $"noise suppression {State(NoiseSuppressionEnabled)}, "
            + $"automatic gain control {State(AutomaticGainControlEnabled)}.";
    }
}
