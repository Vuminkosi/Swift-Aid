using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiGuidanceLiveProviderTests
{
    [LivePlanningFact]
    public async Task SyntheticReportPlanSatisfiesTheStructuredContract()
    {
        var options = EllaiOptions.Bind(Environment.GetEnvironmentVariable);
        using var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(120) };
        var router = new EllaiReasoningRouter(options, new(http, options), new(http, options), new(http, options));
        var planner = new EllaiModelGuidancePlanner(new EllaiProviderTurnClient(router));
        var result = await planner.ComposeAsync("Guide me through completing and submitting a synthetic report.",
            GuidanceMode.Navigation, new(EllaiReportUiManifest.All, "synthetic", "synthetic", false), [], default);
        Assert.True(result.Draft is not null, string.Join("; ", result.Errors));
        Assert.Empty(EllaiDecisionValidator.ValidateDraft(result.Draft!));
    }

    private sealed class LivePlanningFactAttribute : FactAttribute
    {
        public LivePlanningFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1")
                Skip = "Requires the configured live provider; sends synthetic report planning metadata only.";
        }
    }
}
