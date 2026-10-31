using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T03")]
public sealed class EllaiPolicyTests
{
    private readonly EllaiPolicyService _policy = new();

    [Fact]
    public void MicrophoneGrantDoesNotAuthorizeContributorDispatch()
    {
        var request = new EllaiAuthorizationRequest(
            "session-1", EllaiMode.Ask, "cases", "case-1",
            null, AdminActionKind.CreateAdministrativeReviewTask,
            "epoch-1", "consent-1", ContributorEligible: false);

        var result = _policy.Authorize(request, "real-sensitive");

        Assert.False(result.Permitted);
        Assert.NotNull(result.DenialReason);
    }

    [Fact]
    public void ClinicalContentIsRejectedBeforeExecution()
    {
        var request = new EllaiAuthorizationRequest(
            "session-1", EllaiMode.Ask, "triage-symptoms", null,
            UiIntentKind.NavigateFeature, null,
            "epoch-1", "consent-1", ContributorEligible: true);

        var result = _policy.Authorize(request, "synthetic-demo");

        Assert.False(result.Permitted);
        Assert.Contains("Clinical", result.DenialReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GuidanceIntentsRequireEllaiGuidanceFeature()
    {
        var request = new EllaiAuthorizationRequest(
            "session-1", EllaiMode.ShowMe, "cases", null,
            UiIntentKind.StartGuidance, null,
            "epoch-1", "consent-1", ContributorEligible: true);

        var result = _policy.Authorize(request, "synthetic-demo");

        Assert.False(result.Permitted);
    }

    [Fact]
    public void RealSensitiveContentIsNeverContributorEligible()
    {
        Assert.False(EllaiPolicyService.IsContributorEligible("synthetic-demo", "real-sensitive"));
        Assert.False(EllaiPolicyService.IsContributorEligible("synthetic-demo", "unknown-origin"));
        Assert.True(EllaiPolicyService.IsContributorEligible(
            EllaiProcessingClassification.SyntheticDemo, EllaiProcessingClassification.SyntheticDemo));
        Assert.True(EllaiPolicyService.IsContributorEligible(
            EllaiProcessingClassification.ConsentedNonSensitive,
            EllaiProcessingClassification.ConsentedNonSensitive));
        Assert.False(EllaiPolicyService.IsContributorEligible("consented-nonsensitive", "public"));
    }

    [Fact]
    public void PermittedSyntheticReadPasses()
    {
        var request = new EllaiAuthorizationRequest(
            "session-1", EllaiMode.Ask, "cases", "case-1",
            UiIntentKind.HighlightControl, null,
            "epoch-1", "consent-1", ContributorEligible: true);

        var result = _policy.Authorize(request, "synthetic");

        Assert.True(result.Permitted);
        Assert.Equal(EllaiPolicyService.PolicyVersion, result.PolicyVersion);
    }
}
