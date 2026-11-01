using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;
using SwiftAid.UI.Components.Surfaces.Operations.CaseDetail;

namespace SwiftAid.Ui.Tests;

public sealed class CaseReportDisplayTests
{
    [Fact]
    public void PlaceLinkPreservesRegionAndLocalityWithoutTreatingTextAsUrlSyntax()
    {
        var place = new CaseReportPlace("SZ", "Manzini", null, "Place & area", null, null, "SZ:place:test");
        Assert.Equal("Manzini | Place & area", CaseReportDisplay.LocationLabel(place));
        Assert.Equal("/surveillance?country=SZ&place=SZ%3Aplace%3Atest&region=Manzini&area=Place%20%26%20area", CaseReportDisplay.MapHref(place));
    }

    [Fact]
    public void MissingPlaceIsExplicitAndPreviewDoesNotAlterOriginalEvidence()
    {
        var place = new CaseReportPlace("ZW", null, null, null, null, null, null);
        Assert.Equal("Region not recorded | Area not recorded", CaseReportDisplay.LocationLabel(place));
        var original = new string('x', 250);
        var report = new CaseReportDetails(Guid.NewGuid(), 1, null, [original], place);
        Assert.InRange(CaseReportDisplay.ObservationPreview(report).Length, 1, 180);
        Assert.EndsWith("\u2026", CaseReportDisplay.ObservationPreview(report));
        Assert.Equal(original, Assert.Single(report.Observations));
    }

    [Fact]
    public async Task EvidenceRendersVerbatimTextAsTextRatherThanHtml()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CaseReportEvidence>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CaseReportEvidence.Report)] = new CaseReportDetails(Guid.NewGuid(), 2,
                    "Synthetic narrative", ["<script>not executable</script>", "Reported fever"], null)
            }));
            return output.ToHtmlString();
        });
        Assert.Contains("Reported fever", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("revision 2", html);
    }
}
