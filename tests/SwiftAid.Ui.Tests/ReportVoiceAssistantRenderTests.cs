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

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
