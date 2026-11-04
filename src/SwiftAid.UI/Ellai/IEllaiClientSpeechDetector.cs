namespace SwiftAid.UI.Ellai;

/// <summary>Local inference over the existing enhanced PCM capture. No capture ownership.</summary>
public interface IEllaiClientSpeechDetector : IDisposable
{
    float AnalyzeFrame(ReadOnlySpan<byte> pcm24Khz);
}

public interface IEllaiClientSpeechDetectorFactory
{
    IEllaiClientSpeechDetector Create();
    event Action<string>? SpeechConfirmed;
    void NotifySpeechConfirmed(string captureId);
}
