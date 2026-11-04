using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiCaseNavigationPayloadTests
{
    [Theory]
    [InlineData("https://untrusted.example/case/123")]
    [InlineData("../admin")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("6d8cbf5047f64b399ad87c2324401679")]
    public void RejectsNonCanonicalResourceTargets(string resource)
    {
        var proposal = new UiIntentProposal(UiIntentKind.OpenAuthorizedRecord, "case-detail", resource, null,
            new Dictionary<string, string>());
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal]));
    }

    [Fact]
    public void AcceptsOnlyOneStrictCaseTarget()
    {
        var proposal = new UiIntentProposal(UiIntentKind.OpenAuthorizedRecord, "case-detail",
            "6d8cbf50-47f6-4b39-9ad8-7c2324401679", null, new Dictionary<string, string>());
        Assert.NotNull(EllaiUiProposalPayload.Serialize([proposal]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with { FeatureId = "admin" }]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with { ControlId = "arbitrary" }]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with
        { Arguments = new Dictionary<string, string> { ["authorized"] = "true" } }]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal, proposal]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal,
            new(UiIntentKind.SelectTab, "case-detail", null, "case-detail.tabs", new Dictionary<string, string> { ["tab"] = "overview" })]));
    }
}
