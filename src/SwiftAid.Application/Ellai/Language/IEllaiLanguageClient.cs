namespace SwiftAid.Application.Ellai;

public sealed record EllaiTranslationRequest(string RequestId, string Text, string SourceLanguage, string TargetLanguage);
public sealed record EllaiTranslationResult(string RequestId, string Text, string SourceLanguage, string TargetLanguage,
    string ModelId, string ModelRevision, int InputTokens, double InferenceMilliseconds, string Quality);
public sealed record EllaiNativeTranscriptionRequest(string RequestId, string Language, string PcmBase64, int SampleRate);
public sealed record EllaiNativeTranscriptionResult(string RequestId, string Text, string Language,
    string ModelId, string ModelRevision, double InferenceMilliseconds, string Quality);

/// <summary>Private inference only; the caller owns authorization, durable outcomes and review.</summary>
public interface IEllaiLanguageClient
{
    bool IsConfigured { get; }
    Task<EllaiTranslationResult> TranslateAsync(EllaiTranslationRequest request, CancellationToken cancellationToken);
    Task<EllaiNativeTranscriptionResult> TranscribeAsync(EllaiNativeTranscriptionRequest request, CancellationToken cancellationToken);
}

public sealed class EllaiLanguageProviderException(string code, bool retryable) : Exception(code)
{
    public bool Retryable { get; } = retryable;
}
