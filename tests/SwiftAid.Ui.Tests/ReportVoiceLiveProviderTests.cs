using System.Buffers.Binary;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Intake;
using SwiftAid.Intelligence.Meta;
using SwiftAid.UI.Components.Public;
using SwiftAid.UI.Components.Public.GuidedReportStepper;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

/// <summary>Opt-in synthetic speech through real ASR, routed reasoning and the rendered report bridge.</summary>
public sealed class ReportVoiceLiveProviderTests
{
    [SyntheticSpeechFact]
    public async Task RecordedAndRealtimeSpeechPopulateRenderedReport()
    {
        var options = EllaiOptions.Bind(Environment.GetEnvironmentVariable);
        Assert.Empty(options.Validate());
        var wav = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("SWIFTAID_TEST_SYNTHETIC_REPORT_WAV")!);
        Assert.True(wav.Length > 44 && wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            wav.AsSpan(36, 4).SequenceEqual("data"u8) &&
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20, 2)) == 1 &&
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4)) == 24000 &&
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34, 2)) == 16);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var uploadStream = new MemoryStream(wav, writable: false);
        var upload = await new MetaAsrUploadAdapter(http, options)
            .TranscribeAsync(uploadStream, "synthetic-report.wav", "audio/wav", default);
        using var realtimeStream = new MemoryStream(wav, 44, wav.Length - 44, writable: false);
        var realtime = await new MetaAsrRealtimeAdapter(options).TranscribePcmAsync(realtimeStream, default);
        var router = new EllaiReasoningRouter(options, new EllaiReasoningAdapter(http, options),
            new OllamaEllaiReasoningProvider(http, options), new ServerlessEllaiReasoningProvider(http, options));
        var capture = new CapturingClient(new EllaiProviderTurnClient(router));
        var extraction = new ReportVoiceExtraction(capture);
        foreach (var transcript in new[] { upload.Transcript, string.Join(" ", realtime.FinalTranscripts) })
        {
            Assert.False(string.IsNullOrWhiteSpace(transcript));
            var result = await extraction.ExtractAsync(transcript, default);
            Assert.True(result.Available, $"Extraction unavailable; finish: {capture.Last!.FinishReason}; completion tokens: {capture.Last.CompletionTokens}.");
            var draft = await FillRenderedReportAsync(transcript, result);
            Assert.Equal("35", draft.AgeYearsText);
            var locality = result.Fields.SingleOrDefault(field => field.Field == "locality");
            Assert.NotNull(locality);
            Assert.Equal(locality.Value, draft.Locality, ignoreCase: true);
            Assert.True(transcript.Contains(draft.Locality!, StringComparison.Ordinal));
            Assert.Equal(transcript, draft.Narrative);
            Assert.False(draft.CountryConfirmed);
        }
    }

    private static async Task<PublicReportDraft> FillRenderedReportAsync(string transcript, ReportVoiceExtractionResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoOpJsRuntime>();
        var context = new EllaiPublicReportDraftContext();
        services.AddSingleton(context);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human };
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<GuidedReportStepper>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(GuidedReportStepper.Draft)] = draft,
                [nameof(GuidedReportStepper.Countries)] = new[] { new PublicCountryOption("ZM", "Zambia") },
                [nameof(GuidedReportStepper.CountryCatalogueVersion)] = "fixture",
                [nameof(GuidedReportStepper.CountryCatalogueIsCurrent)] = true
            }));
            context.RegisterVoiceController(new object(), _ => { context.BindVoiceCapture("capture"); return Task.FromResult(true); },
                () => Task.CompletedTask);
            await context.StartReportVoiceAsync(EllaiVoiceCaptureMode.Live);
            var lease = context.AdmitVoiceTurn("session", "turn", "capture")!;
            Assert.True(context.AppendVoiceTranscript(lease.Generation, transcript));
            context.CompleteVoiceTurn(lease.Generation, transcript, new("Ready", result.Fields
                .Select(field => new ReportVoiceSuggestion(field.Field, field.Value, field.Evidence)).ToArray()));
        });
        return draft;
    }

    private sealed class CapturingClient(IEllaiTurnCompletionClient inner) : IEllaiTurnCompletionClient
    {
        public EllaiTurnCompletion? Last { get; private set; }
        public Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct) =>
            inner.CompleteAsync(messages, maxTokens, ct);
        public async Task<EllaiTurnCompletion> CompleteReportVoiceAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct) =>
            Last = await inner.CompleteReportVoiceAsync(messages, maxTokens, ct);
    }

    private sealed class SyntheticSpeechFactAttribute : FactAttribute
    {
        public SyntheticSpeechFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1" ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_SYNTHETIC_REPORT_WAV")))
                Skip = "Set ELLAI_LIVE=1 and SWIFTAID_TEST_SYNTHETIC_REPORT_WAV to synthetic 24 kHz speech.";
        }
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
