namespace SwiftAid.Contracts.Ellai;

/// <summary>Bounded conversational controls. No routes, record IDs, approvals or executable arguments.</summary>
public static class EllaiGuidanceControlProposal
{
    public static string? ReadCommand(UiIntentProposal? proposal) =>
        proposal is { Kind: UiIntentKind.ControlGuidance, FeatureId: "ellai.guidance", ResourceId: null, ControlId: null }
        && proposal.Arguments is { Count: 1 }
        && proposal.Arguments.TryGetValue("command", out var command)
        && command is "pause" or "resume" or "cancel" or "stop-speech" or "next" or "back" or "skip"
            ? command : null;
}
