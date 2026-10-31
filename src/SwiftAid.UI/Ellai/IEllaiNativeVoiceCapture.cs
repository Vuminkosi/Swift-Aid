namespace SwiftAid.UI.Ellai;

/// <summary>Optional native live PCM capture for hybrid clients. Implementations
/// must emit bounded little-endian mono PCM16 chunks at 24 kHz and stop without
/// retaining captured audio after the callback returns.</summary>
public interface IEllaiNativeVoiceCapture
{
    bool IsAvailable { get; }
    event Action<string>? Interrupted;
    /// <summary>Raised after sustained local speech is measured while the
    /// platform's echo-cancellation path is active.</summary>
    event Action? SpeechDetected;
    event Action? RouteChanged;

    Task<string> StartAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onPcmChunk,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SuspendAsync(CancellationToken cancellationToken = default);
}
