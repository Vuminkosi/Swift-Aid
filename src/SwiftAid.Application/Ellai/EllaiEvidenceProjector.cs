using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Source-backed compact explanation for the evidence timeline:
/// what was observed (versions), why the next action was selected, what was
/// approved/attempted, which receipt confirms it, what remains unverified.
/// Rationale only — never hidden chain-of-thought. (Evidence groundwork.)</summary>
public sealed record EllaiExplanation(
    string Observed,
    string Rationale,
    string ApprovalState,
    string VerificationState,
    IReadOnlyList<EvidenceRef> Sources);

public static class EllaiEvidenceProjector
{
    public static EllaiExplanation Project(
        EllaiDecision decision, IReadOnlyList<EvidenceRef> receipts, IReadOnlyList<string> unverified)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(receipts);

        var observed = decision.Claims.Count == 0
            ? "No new observations this turn."
            : string.Join("; ", decision.Claims.Select(c =>
                $"{c.Text} [{c.Kind}, {c.Evidence.Count} ref(s)]"));

        var approval = decision.PlanProposal is null
            ? decision.UiIntents.Count == 0 ? "No proposals pending approval." : $"{decision.UiIntents.Count} UI proposal(s) awaiting application handling."
            : $"Plan '{decision.PlanProposal.Objective}' with {decision.PlanProposal.Actions.Count} action(s) awaiting exact human approval.";

        var verification = receipts.Count == 0
            ? "No receipts confirmed."
            : $"{receipts.Count} receipt(s) confirmed: {string.Join(", ", receipts.Select(r => r.EvidenceId))}.";

        if (unverified.Count > 0)
        {
            verification += $" Unverified: {string.Join(", ", unverified)}.";
        }

        return new EllaiExplanation(
            observed,
            $"Selected {decision.Kind} under the current scope and remaining limits.",
            approval, verification,
            decision.Claims.SelectMany(c => c.Evidence).ToList());
    }
}
