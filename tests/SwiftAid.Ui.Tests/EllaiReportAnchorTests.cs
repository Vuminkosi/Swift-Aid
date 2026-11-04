using MudBlazor.Services;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Components.Public;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiReportAnchorTests
{
    private sealed class StaticJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static render must not invoke JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("Static render must not invoke JavaScript.");
    }

    [Fact]
    public void ConfirmationTargetsItsOwnControlInsteadOfReviewHeading()
    {
        var review = EllaiReportUiManifest.All.Single(c => c.ControlId == "report.review");
        var confirmation = EllaiReportUiManifest.All.Single(c => c.ControlId == "report.confirmations");
        Assert.NotEqual(review.AnchorBindingKey, confirmation.AnchorBindingKey);
    }

    [Fact]
    public async Task PersistedReceiptRendersTheAnchorUsedByGeneratedGuidance()
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime>(new StaticJsRuntime()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<PublicReportReceipt>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PublicReportReceipt.Receipt)] = new IntakeReceipt(Guid.NewGuid(), Guid.NewGuid(),
                    IntakeStatus.ReceivedUnverified, IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow)
            }));
            return output.ToHtmlString();
        });
        var descriptor = EllaiReportUiManifest.All.Single(c => c.ControlId == "report.receipt");
        Assert.Contains($"data-bonboarding-anchor=\"{descriptor.AnchorBindingKey}\"", html);
        Assert.Contains("data-ellai-control=\"report.receipt\"", html);
    }
}
