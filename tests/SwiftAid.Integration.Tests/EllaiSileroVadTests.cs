using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using Xunit.Abstractions;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiSileroVadTests
{
    private readonly ITestOutputHelper _output;

    public EllaiSileroVadTests(ITestOutputHelper output)
    {
        _output = output;
    }

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
    public async Task ConcurrentSessionsKeepIndependentStateOnTheSharedOnnxModel()
    {
        using var detector = new SileroOnnxVoiceActivityDetector();
        const int frameCount = 32;
        var silence = new float[512];
        var voicedTone = Enumerable.Range(0, 512)
            .Select(index => 0.35f * MathF.Sin(2f * MathF.PI * 210f * index / 16_000f))
            .ToArray();

        async Task<VoiceActivityResult[]> AnalyzeSequenceAsync(
            IVoiceActivityDetectionSession session,
            float[] input)
        {
            var results = new VoiceActivityResult[frameCount];
            for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                results[frameIndex] = await session.AnalyzeAsync(
                    CreateFrame(frameIndex, input));
            }
            return results;
        }

        VoiceActivityResult[][] expected = new VoiceActivityResult[2][];
        using (var silenceSession = detector.CreateSession())
        {
            expected[0] = await AnalyzeSequenceAsync(silenceSession, silence);
        }
        using (var toneSession = detector.CreateSession())
        {
            expected[1] = await AnalyzeSequenceAsync(toneSession, voicedTone);
        }

        var concurrentSessions = Enumerable.Range(0, 12)
            .Select(sessionIndex => Task.Run(async () =>
            {
                using var session = detector.CreateSession();
                var fixtureIndex = sessionIndex % expected.Length;
                var input = fixtureIndex == 0 ? silence : voicedTone;
                return await AnalyzeSequenceAsync(session, input);
            }))
            .ToArray();
        var actual = await Task.WhenAll(concurrentSessions);

        for (var sessionIndex = 0; sessionIndex < actual.Length; sessionIndex++)
        {
            var fixtureIndex = sessionIndex % expected.Length;
            Assert.Equal(
                expected[fixtureIndex].Select(result => result.SpeechProbability),
                actual[sessionIndex].Select(result => result.SpeechProbability));
            Assert.All(actual[sessionIndex], result => Assert.False(result.StateWasReset));
        }
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
    public async Task InferenceAndFrameGapMetricsContainNoAudioOrSessionDimensions()
    {
        var inferenceMeasurements = new List<double>();
        long frameGaps = 0;
        var nonEmptyTagSets = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SwiftAid.Gateway.Ellai.Voice")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "swiftaid.ellai.voice.vad.inference.duration")
            {
                inferenceMeasurements.Add(measurement);
            }
            if (tags.Length != 0)
            {
                nonEmptyTagSets++;
            }
        });
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "swiftaid.ellai.voice.audio.frame.sequence.gap")
            {
                frameGaps += measurement;
            }
            if (tags.Length != 0)
            {
                nonEmptyTagSets++;
            }
        });
        listener.Start();

        using var detector = new SileroOnnxVoiceActivityDetector();
        using var session = detector.CreateSession();
        var baselineStarted = Stopwatch.GetTimestamp();
        for (var frameIndex = 0; frameIndex < 200; frameIndex++)
        {
            var sequence = frameIndex < 100 ? frameIndex : frameIndex + 1;
            await session.AnalyzeAsync(CreateFrame(sequence, new float[512]));
        }
        var elapsed = Stopwatch.GetElapsedTime(baselineStarted);

        Assert.Equal(200, inferenceMeasurements.Count);
        Assert.All(inferenceMeasurements, measurement => Assert.True(double.IsFinite(measurement) && measurement >= 0));
        Assert.Equal(1, frameGaps);
        Assert.Equal(0, nonEmptyTagSets);

        var sortedInferenceTimes = inferenceMeasurements.Order().ToArray();
        var modelPath = Path.Combine(
            AppContext.BaseDirectory, "Ellai", "Voice", "Models", "silero_vad_16k_op15.onnx");
        var modelHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(modelPath)));
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "Synthetic Silero CPU baseline: model=silero_vad_16k_op15.onnx "
            + "sha256={0} frames=200 elapsed_ms={1:F2} logical_processors={2} "
            + "inference_p50_ms={3:F3} inference_p95_ms={4:F3}",
            modelHash,
            elapsed.TotalMilliseconds,
            Environment.ProcessorCount,
            Percentile(sortedInferenceTimes, 0.50),
            Percentile(sortedInferenceTimes, 0.95)));
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile) =>
        values[Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1)];

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
