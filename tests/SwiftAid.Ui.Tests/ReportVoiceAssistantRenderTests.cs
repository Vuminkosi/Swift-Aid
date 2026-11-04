using SwiftAid.Application.Ellai;
using SwiftAid.Application.Intake;
using Microsoft.AspNetCore.Components;
using SwiftAid.Contracts.Intake;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Geography;
using SwiftAid.UI.Components.Public;
using SwiftAid.UI.Components.Public.GuidedReportStepper;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using SwiftAid.UI.Components.Surfaces.Public;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed class ReportVoiceAssistantRenderTests
{
    [Fact]
    public async Task VoiceChoicesShowProcessingNoticeAndRequireExplicitConsent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoOpJsRuntime>();
        services.AddSingleton<EllaiPublicReportDraftContext>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ReportVoiceAssistant>()).ToHtmlString());

        Assert.Contains("sent for transcription and AI-assisted extraction", html);
        Assert.Contains("I agree to send my speech and transcript", html);
        Assert.Contains("Speak live", html);
        Assert.Contains("Record then transcribe", html);
        Assert.Contains("disabled", html);
    }

    [Fact]
    public async Task CountryVoiceSuggestionFillsFormWithoutConfirmingOrClearingLocality()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoOpJsRuntime>();
        var context = new EllaiPublicReportDraftContext();
        services.AddSingleton(context);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var draft = new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human,
            Locality = "Mbabane", LocalityPresence = PresenceState.Known };
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<GuidedReportStepper>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(GuidedReportStepper.Draft)] = draft,
                [nameof(GuidedReportStepper.Countries)] = new[] { new PublicCountryOption("SZ", "Eswatini") },
                [nameof(GuidedReportStepper.CountryCatalogueVersion)] = "fixture",
                [nameof(GuidedReportStepper.CountryCatalogueIsCurrent)] = true
            }));
            context.RegisterVoiceController(new object(), _ =>
            {
                context.BindVoiceCapture("capture");
                return Task.FromResult(true);
            }, () => Task.CompletedTask);
            await context.StartReportVoiceAsync(EllaiVoiceCaptureMode.Live);
            var lease = context.AdmitVoiceTurn("session", "turn", "capture")!;
            const string transcript = "This happened in Eswatini.";
            context.CompleteVoiceTurn(lease.Generation, transcript,
                new("Ready", [new("country", "Eswatini", "This happened in Eswatini")]));
        });
        Assert.Equal("SZ", draft.CountryCode);
        Assert.False(draft.CountryConfirmed);
        Assert.Equal("Mbabane", draft.Locality);
    }

    [Fact]
    public async Task ShortModelExcerptsFillMultipleFieldsInRenderedReport()
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
        const string transcript = "The patient is 35 years old. She lives in Mbabane.";
        var extraction = await new ReportVoiceExtraction(new ShortExcerptCompletion()).ExtractAsync(transcript, default);
        Assert.True(extraction.Available);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<GuidedReportStepper>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(GuidedReportStepper.Draft)] = draft
            }));
            context.RegisterVoiceController(new object(), _ =>
            {
                context.BindVoiceCapture("capture");
                return Task.FromResult(true);
            }, () => Task.CompletedTask);
            await context.StartReportVoiceAsync(EllaiVoiceCaptureMode.Live);
            var lease = context.AdmitVoiceTurn("session", "turn", "capture")!;
            Assert.True(context.AppendVoiceTranscript(lease.Generation, transcript));
            context.CompleteVoiceTurn(lease.Generation, transcript, new("Ready", extraction.Fields
                .Select(field => new ReportVoiceSuggestion(field.Field, field.Value, field.Evidence)).ToArray()));
        });
        Assert.Equal("35", draft.AgeYearsText);
        Assert.Equal("Mbabane", draft.Locality);
        Assert.Equal(transcript, draft.Narrative);
    }

    private sealed class ShortExcerptCompletion : IEllaiTurnCompletionClient
    {
        public Task<EllaiTurnCompletion> CompleteAsync(IReadOnlyList<EllaiChatMessage> messages, int maxTokens, CancellationToken ct) =>
            Task.FromResult(new EllaiTurnCompletion("""{"fields":[{"field":"age","value":"35","evidence":"35 years old"},{"field":"locality","value":"Mbabane","evidence":"Mbabane"}]}""", 0, 0));
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
