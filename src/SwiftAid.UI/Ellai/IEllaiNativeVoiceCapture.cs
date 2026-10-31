namespace SwiftAid.UI.Ellai;

/// <summary>Optional native live PCM capture for hybrid clients. Implementations
/// must emit bounded little-endian mono PCM16 chunks at 24 kHz and stop without
/// retaining captured audio after the callback returns.</summary>
public interface IEllaiNativeVoiceCapture
{
    bool IsAvailable { get; }
    bool IsEchoCancellationEnabled => false;
    EllaiAudioProcessingStatus AudioProcessingStatus => EllaiAudioProcessingStatus.Unknown;
    event Action<string>? Interrupted;
    event Action? RouteChanged;

    Task<string> StartAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onPcmChunk,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SuspendAsync(CancellationToken cancellationToken = default);
}
