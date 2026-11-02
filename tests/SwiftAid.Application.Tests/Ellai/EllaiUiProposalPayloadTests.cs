using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiUiProposalPayloadTests
{
    [Theory]
    [InlineData("pause", true)]
    [InlineData("resume", true)]
    [InlineData("cancel", true)]
    [InlineData("stop-speech", true)]
    [InlineData("back", true)]
    [InlineData("next", true)]
    [InlineData("approve", false)]
    [InlineData("complete", false)]
    public void GuidanceControlAllowsOnlyBoundedNonBusinessCommands(string command, bool valid)
    {
        var proposal = new UiIntentProposal(UiIntentKind.ControlGuidance, "ellai.guidance", null, null,
            new Dictionary<string, string> { ["command"] = command });
        if (valid) Assert.NotNull(EllaiUiProposalPayload.Serialize([proposal]));
        else Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with { ResourceId = "private-case" }]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with
        {
            Arguments = new Dictionary<string, string> { ["command"] = "pause", ["approval"] = "granted" },
        }]));
    }

    [Theory]
    [InlineData("overview", true)]
    [InlineData("attachments", true)]
    [InlineData("observations", true)]
    [InlineData("../admin", false)]
    [InlineData("delete", false)]
    public void TabSelectionIsRestrictedToRegisteredPresentationTargets(string tab, bool valid)
    {
        var proposal = new UiIntentProposal(UiIntentKind.SelectTab, "case-detail", null, "case-detail.tabs",
            new Dictionary<string, string> { ["tab"] = tab });
        if (valid) Assert.NotNull(EllaiUiProposalPayload.Serialize([proposal]));
        else Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with { ResourceId = "another-record" }]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with { FeatureId = "admin" }]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal, proposal]));
    }

    [Fact]
    public void BoundedProposalsRoundTripWithoutBecomingReceipts()
    {
        var payload = EllaiUiProposalPayload.Serialize([new(UiIntentKind.StartGuidance, "report", null, null,
            new Dictionary<string, string> { ["objective"] = "Create a report" })]);
        var proposal = Assert.Single(EllaiUiProposalPayload.Deserialize(payload!));
        Assert.Equal(UiIntentKind.StartGuidance, proposal.Kind);
        Assert.Equal("Create a report", proposal.Arguments["objective"]);
        var resolution = EllaiUtteranceResolution.Completed("I can guide you.", 1) with { UiProposalsJson = payload };
        resolution.Validate();
        Assert.Throws<ArgumentException>(() => (EllaiUtteranceResolution.Cancelled with { UiProposalsJson = payload }).Validate());
    }

    [Fact]
    public void InvalidAndExcessiveProposalsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([new((UiIntentKind)999, "report", null, null, new Dictionary<string, string>())]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([new(UiIntentKind.StartGuidance, "report", null, null,
            new Dictionary<string, string> { ["objective"] = new string('x', 2001) })]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Deserialize("null"));
        Assert.Throws<System.Text.Json.JsonException>(() => EllaiUiProposalPayload.Deserialize("[{\"kind\":999}]"));
        Assert.Null(EllaiUiProposalPayload.Serialize([]));
    }
}
