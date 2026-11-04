using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiConversationProjectionTests
{
    [Fact]
    public void ReconnectPreservesProposalIdentityAndSeparatesSessions()
    {
        var turn = new EllaiConversationTurn("session-a", "turn-1", 1, "Help", "I can guide you", "Completed", 1, DateTimeOffset.UtcNow)
        {
            UiProposalsJson = EllaiUiProposalPayload.Serialize([new(UiIntentKind.StartGuidance, "ellai.guidance", null, null,
                new Dictionary<string, string> { ["objective"] = "Create a report" })])
        };
        var first = Assert.Single(EllaiConversationProjection.ToClient(turn).UiProposals);
        var recovered = Assert.Single(EllaiConversationProjection.ToClient(turn).UiProposals);
        Assert.Equal(first.ProposalId, recovered.ProposalId);
        Assert.NotEqual(first.ProposalId, Assert.Single(EllaiConversationProjection.ToClient(turn with { SessionId = "session-b" }).UiProposals).ProposalId);
        Assert.Empty(EllaiConversationProjection.ToClient(turn with { Outcome = "Cancelled" }).UiProposals);
        Assert.Empty(EllaiConversationProjection.ToClient(turn with { UiProposalsJson = null }).UiProposals);
    }
}
