using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

/// <summary>Live provider probes. Run only with the Ellai section configured
/// in the environment; otherwise each test skips (never a mock pass).
/// Nothing sensitive is asserted into output: only route, model identity,
/// status fields, durations and usage counts are recorded.</summary>
[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T24")]
[Trait("Live", "Provider")]
public sealed class EllaiLiveProbeTests
{
    private static EllaiOptions LiveOptions()
    {
        var options = EllaiOptions.Bind(Environment.GetEnvironmentVariable);
        if (!options.Enabled || options.Validate().Count != 0)
        {
            throw new InvalidOperationException("Ellai live configuration is absent or invalid.");
        }

        return options;
    }

    /// <summary>Runs only when explicitly opted in; otherwise skipped at
    /// discovery so the default suite never mock-passes a live gate.</summary>
    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("ELLAI_LIVE"), "1", StringComparison.Ordinal))
            {
                Skip = "Set ELLAI_LIVE=1 with Ellai configuration present to run live provider probes.";
            }
        }
    }

    [LiveFact]
    public async Task LiveReasoningReturnsStopWithConfiguredModel()
    {
        var options = LiveOptions();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var adapter = new EllaiReasoningAdapter(http, options!);
        var result = await adapter.CompleteAsync(
            [new SwiftAid.Application.Ellai.EllaiChatMessage("user", "Reply with the single word: ok")],
            1024, CancellationToken.None);

        Assert.Equal("stop", result.FinishReason);
        Assert.Equal(options!.ReasoningModel, result.Model);
        Assert.Contains("ok", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Usage.TotalTokens > 0);
    }

    [LiveFact]
    public async Task LiveAsrUploadTranscribesSyntheticTone()
    {
        var options = LiveOptions();

        using var tone = SyntheticToneWav(16000, 2, 440);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var adapter = new MetaAsrUploadAdapter(http, options!);
        var result = await adapter.TranscribeAsync(tone, "tone.wav", "audio/wav", CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        Assert.True(result.AudioDurationMs >= 1500);
        Assert.NotNull(result.Transcript);
    }

    [LiveFact]
    public async Task LiveRealtimeAsrStreamsSyntheticTone()
    {
        var options = LiveOptions();
        var adapter = new MetaAsrRealtimeAdapter(options);
        using var pcm = SyntheticTonePcm(24000, 2, 440);
        var result = await adapter.TranscribePcmAsync(pcm, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        Assert.NotNull(result.FinalTranscripts);
    }

    private static MemoryStream SyntheticTonePcm(int sampleRate, int seconds, int frequency)
    {
        var stream = new MemoryStream();
        var frames = sampleRate * seconds;
        foreach (var t in Enumerable.Range(0, frames))
        {
            var sample = (short)(12000 * Math.Sin(2 * Math.PI * frequency * t / sampleRate));
            stream.Write(BitConverter.GetBytes(sample));
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream SyntheticToneWav(int sampleRate, int seconds, int frequency)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var frames = sampleRate * seconds;
            var dataSize = frames * 2;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            for (var t = 0; t < frames; t++)
            {
                writer.Write((short)(12000 * Math.Sin(2 * Math.PI * frequency * t / sampleRate)));
            }
        }

        stream.Position = 0;
        return stream;
    }
}
