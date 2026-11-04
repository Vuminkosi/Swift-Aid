using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiFeatureNavigationPayloadTests
{
    [Theory]
    [InlineData("navigation.monitoring", true)]
    [InlineData("navigation.cases", true)]
    [InlineData("navigation.unknown", false)]
    [InlineData("https://example.com", false)]
    [InlineData("../admin", false)]
    public void OnlyRegisteredControlDestinationsAreAccepted(string control, bool valid)
    {
        var proposal = new UiIntentProposal(UiIntentKind.NavigateFeature, "navigation", null, control, new Dictionary<string, string>());
        if (valid) Assert.NotNull(EllaiUiProposalPayload.Serialize([proposal]));
        else Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with { ResourceId = "case-id" }]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal, proposal]));
    }
    [Fact]
    public void BackDoesNotAcceptRoutesOrExecutionAuthority()
    {
        var proposal = new UiIntentProposal(UiIntentKind.NavigateFeature, "navigation", null, null,
            new Dictionary<string, string> { ["command"] = "back" });
        Assert.NotNull(EllaiUiProposalPayload.Serialize([proposal]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with { ControlId = "navigation.cases" }]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with
            { Arguments = new Dictionary<string, string> { ["command"] = "back", ["url"] = "/admin" } }]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with
            { Arguments = new Dictionary<string, string> { ["command"] = "undo" } }]));
    }
}
