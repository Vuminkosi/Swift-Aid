namespace SwiftAid.Application.Ellai;

/// <summary>One ordered mono floating-point audio frame for server-side VAD.</summary>
public sealed record VoiceActivityFrame(
    long Sequence,
    int SampleRate,
    ReadOnlyMemory<float> Samples);

/// <summary>Acoustic activity estimate. It is not a conversational turn decision.</summary>
public sealed record VoiceActivityResult(
    long Sequence,
    float SpeechProbability,
    bool IsSpeech,
    bool StateWasReset);

/// <summary>Creates isolated recurrent inference state for each voice stream.</summary>
public interface IVoiceActivityDetector
{
    IVoiceActivityDetectionSession CreateSession();
}

public interface IVoiceActivityDetectionSession : IDisposable
{
    ValueTask<VoiceActivityResult> AnalyzeAsync(
        VoiceActivityFrame frame,
        CancellationToken cancellationToken = default);

    void Reset();
}
