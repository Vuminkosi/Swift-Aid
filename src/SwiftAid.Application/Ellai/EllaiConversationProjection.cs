using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Shared projection for owner-authorized HTTP and in-process conversation reads.
/// Proposal identities are stable across reconnects; they confer no execution authority.</summary>
public static class EllaiConversationProjection
{
    public static EllaiConversationTurnDto ToClient(EllaiConversationTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var proposals = turn.Outcome == "Completed" && turn.UiProposalsJson is not null
            ? EllaiUiProposalPayload.Deserialize(turn.UiProposalsJson)
            : [];
        return new(turn.ClientUtteranceId, turn.Sequence, turn.UserText, turn.AssistantText,
            turn.Outcome, turn.AttemptCount, turn.CompletedAtUtc)
        {
            UiProposals = proposals.Select((proposal, index) => new EllaiUiProposalDto(
                $"{turn.SessionId}:{turn.ClientUtteranceId}:{index}", proposal)).ToArray()
        };
    }
}
