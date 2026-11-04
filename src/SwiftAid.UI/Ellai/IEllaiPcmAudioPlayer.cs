namespace SwiftAid.UI.Ellai;

/// <summary>Platform playback for verified PCM received from the Gateway.</summary>
public interface IEllaiPcmAudioPlayer
{
    bool IsAvailable { get; }

    /// <summary>Starts playback from a bounded, transient PCM stream without
    /// buffering the complete Gateway response in memory.</summary>
    Task PlayAsync(Stream pcm, int sampleRate, CancellationToken cancellationToken);

    async Task PlayAsync(byte[] pcm, int sampleRate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        await using var stream = new MemoryStream(pcm, writable: false);
        await PlayAsync(stream, sampleRate, cancellationToken).ConfigureAwait(false);
    }

    Task StopAsync();
}
