using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;
using System.Buffers.Binary;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiSileroVadTests
{
    [Fact]
    public async Task SileroModelAcceptsFramesAndRecognizesDigitalSilence()
    {
        using var detector = new SileroOnnxVoiceActivityDetector();
        using var session = detector.CreateSession();

        var result = await session.AnalyzeAsync(CreateFrame(0, new float[512]));

        Assert.InRange(result.SpeechProbability, 0f, 1f);
        Assert.False(result.IsSpeech);
        Assert.False(result.StateWasReset);
    }

    [Fact]
    public async Task SessionsKeepIndependentRecurrentStateAndResetAfterSequenceGap()
    {
        using var detector = new SileroOnnxVoiceActivityDetector();
        using var first = detector.CreateSession();
        using var second = detector.CreateSession();
        using var control = detector.CreateSession();
        var excitation = Enumerable.Range(0, 512)
            .Select(index => 0.4f * MathF.Sin(2f * MathF.PI * 190f * index / 16_000f))
            .ToArray();

        await first.AnalyzeAsync(CreateFrame(0, excitation));
        var independent = await second.AnalyzeAsync(CreateFrame(0, new float[512]));
        var firstAfterGap = await first.AnalyzeAsync(CreateFrame(2, new float[512]));
        var controlResult = await control.AnalyzeAsync(CreateFrame(0, new float[512]));

        Assert.True(firstAfterGap.StateWasReset);
        Assert.False(independent.StateWasReset);
        Assert.False(controlResult.StateWasReset);
        Assert.Equal(controlResult.SpeechProbability, independent.SpeechProbability);
        Assert.Equal(controlResult.SpeechProbability, firstAfterGap.SpeechProbability);
    }

    [Fact]
    public async Task SessionRejectsWrongFrameRateAndFrameSize()
    {
        using var detector = new SileroOnnxVoiceActivityDetector();
        using var session = detector.CreateSession();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.AnalyzeAsync(new VoiceActivityFrame(0, 24_000, new float[512])));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.AnalyzeAsync(new VoiceActivityFrame(0, 16_000, new float[511])));
    }

    [Fact]
    public async Task SessionRejectsDuplicateAndOutOfOrderFrames()
    {
        using var detector = new SileroOnnxVoiceActivityDetector();
        using var session = detector.CreateSession();
        await session.AnalyzeAsync(CreateFrame(0, new float[512]));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.AnalyzeAsync(CreateFrame(0, new float[512])));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await session.AnalyzeAsync(CreateFrame(-1, new float[512])));
    }

    [Fact]
    public async Task AudioStreamResamplesChunkedPcm24KhzAndMaintainsFrameSequence()
    {
        using var detector = new SileroOnnxVoiceActivityDetector();
        using var stream = new SileroVoiceActivityAudioStream(detector);
        var pcm = new byte[24_000 * sizeof(short)];
        for (var sampleIndex = 0; sampleIndex < 24_000; sampleIndex++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(sampleIndex * sizeof(short), sizeof(short)),
                (short)(8_000 * MathF.Sin(2f * MathF.PI * 250f * sampleIndex / 24_000f)));
        }

        var first = await stream.AppendPcm16Async(pcm.AsMemory(0, 12_000), 24_000);
        var second = await stream.AppendPcm16Async(pcm.AsMemory(12_000), 24_000);
        var results = first.Concat(second).ToArray();

        Assert.Equal(31, results.Length);
        Assert.Equal(Enumerable.Range(0, results.Length).Select(index => (long)index),
            results.Select(result => result.Sequence));
        Assert.All(results, result => Assert.InRange(result.SpeechProbability, 0f, 1f));
    }

    [Fact]
    public async Task AudioStreamResetsVADStateWhenSourceSampleRateChanges()
    {
        using var detector = new SileroOnnxVoiceActivityDetector();
        using var stream = new SileroVoiceActivityAudioStream(detector);

        await stream.AppendPcm16Async(new byte[1_200], 24_000);
        var results = await stream.AppendPcm16Async(new byte[1_200], 16_000);

        Assert.NotEmpty(results);
        Assert.True(results[0].StateWasReset);
        Assert.Equal(0, results[0].Sequence);
    }

    private static VoiceActivityFrame CreateFrame(long sequence, float[] samples) =>
        new(sequence, 16_000, samples);
}
