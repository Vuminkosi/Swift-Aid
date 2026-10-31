namespace SwiftAid.UI.Ellai;

/// <summary>Platform playback for verified PCM received from the Gateway.</summary>
public interface IEllaiPcmAudioPlayer
{
    bool IsAvailable { get; }

    Task PlayAsync(byte[] pcm, int sampleRate, CancellationToken cancellationToken);

    Task StopAsync();
}
