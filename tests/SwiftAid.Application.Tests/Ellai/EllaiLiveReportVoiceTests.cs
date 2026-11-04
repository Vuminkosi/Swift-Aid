using System.Buffers.Binary;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Intake;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

/// <summary>Opt-in real provider check with caller-supplied synthetic 24 kHz speech.</summary>
public sealed class EllaiLiveReportVoiceTests
{
    [SyntheticReportLiveFact]
    public async Task RecordedAndRealtimeSpeechProduceGroundedReportSuggestions()
    {
        var options = EllaiOptions.Bind(Environment.GetEnvironmentVariable);
        Assert.Empty(options.Validate());
        var path = Environment.GetEnvironmentVariable("SWIFTAID_TEST_SYNTHETIC_REPORT_WAV")!;
        var wav = await File.ReadAllBytesAsync(path);
        Assert.True(wav.Length > 44 && wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            wav.AsSpan(8, 4).SequenceEqual("WAVE"u8) && wav.AsSpan(36, 4).SequenceEqual("data"u8) &&
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20, 2)) == 1 &&
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4)) == 24000 &&
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34, 2)) == 16);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var recorded = new MemoryStream(wav, writable: false);
        var upload = await new MetaAsrUploadAdapter(http, options)
            .TranscribeAsync(recorded, "synthetic-report.wav", "audio/wav", CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(upload.Transcript));
        using var realtimePcm = new MemoryStream(wav, 44, wav.Length - 44, writable: false);
        var realtime = await new MetaAsrRealtimeAdapter(options).TranscribePcmAsync(realtimePcm, CancellationToken.None);
        var realtimeText = string.Join(" ", realtime.FinalTranscripts);
        Assert.False(string.IsNullOrWhiteSpace(realtimeText));
        var reasoner = new LiveReasoningClient(http, options);
        var extraction = new ReportVoiceExtraction(reasoner);
        var recordedFields = await extraction.ExtractAsync(upload.Transcript, CancellationToken.None);
        Assert.True(recordedFields.Available, $"Recorded extraction unavailable; finish: {reasoner.Results[0].FinishReason}; completion tokens: {reasoner.Results[0].CompletionTokens}.");
        var realtimeFields = await extraction.ExtractAsync(realtimeText, CancellationToken.None);
        Assert.True(realtimeFields.Available, $"Realtime extraction unavailable; finish: {reasoner.Results[1].FinishReason}; completion tokens: {reasoner.Results[1].CompletionTokens}.");
        Assert.Contains("age", recordedFields.Fields.Select(field => field.Field));
        Assert.Contains("locality", recordedFields.Fields.Select(field => field.Field));
        Assert.Contains("age", realtimeFields.Fields.Select(field => field.Field));
        Assert.Contains("locality", realtimeFields.Fields.Select(field => field.Field));
        Assert.All(recordedFields.Fields, field => Assert.True(upload.Transcript.Contains(field.Evidence, StringComparison.Ordinal)));
        Assert.All(realtimeFields.Fields, field => Assert.True(realtimeText.Contains(field.Evidence, StringComparison.Ordinal)));
    }

    private sealed class LiveReasoningClient(HttpClient http, EllaiOptions options) : IEllaiTurnCompletionClient
    {
        public List<EllaiTurnCompletion> Results { get; } = [];
        private EllaiProviderTurnClient CreateClient() => new(new EllaiReasoningRouter(options,
            new EllaiReasoningAdapter(http, options), new OllamaEllaiReasoningProvider(http, options),
            new ServerlessEllaiReasoningProvider(http, options)));

        public Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct) =>
            CreateClient().CompleteAsync(messages, maxTokens, ct);

        public async Task<EllaiTurnCompletion> CompleteReportVoiceAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct)
        {
            var result = await CreateClient().CompleteReportVoiceAsync(messages, maxTokens, ct);
            Results.Add(result);
            return result;
        }
    }

    private sealed class SyntheticReportLiveFactAttribute : FactAttribute
    {
        public SyntheticReportLiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1" ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_SYNTHETIC_REPORT_WAV")))
                Skip = "Set ELLAI_LIVE=1 and SWIFTAID_TEST_SYNTHETIC_REPORT_WAV to a synthetic 24 kHz WAV.";
        }
    }
}
