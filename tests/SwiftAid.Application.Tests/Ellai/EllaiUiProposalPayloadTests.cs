using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiUiProposalPayloadTests
{
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
