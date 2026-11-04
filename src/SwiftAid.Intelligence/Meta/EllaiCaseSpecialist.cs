using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Bounded, read-only case history specialist. The supervisor owns publication.</summary>
public sealed class EllaiCaseSpecialist(IChatClient client, IEllaiFrameworkAgentFactory? agentFactory = null)
{
    public async Task<EllaiSpecialistResult> RunAsync(string task, string trustedPolicy, bool enabled,
        EllaiToolContext parent, EllaiFrameworkUsage usage, Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        CancellationToken ct, Action<int>? onModelCalls = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(invoke);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(task) || task.Length > 2048 || string.IsNullOrWhiteSpace(trustedPolicy))
            return new(EllaiSpecialistOutcome.InvalidTask, null);
        if (parent.Actor is null || !string.Equals(parent.SessionId, usage.SessionId, StringComparison.Ordinal) ||
            !EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.Case, enabled, parent, out var context) ||
            context!.AllowedTools is not { Count: > 0 })
            return new(EllaiSpecialistOutcome.Denied, null);

        context = context with { RemainingCalls = Math.Min(context.RemainingCalls, 2) };
        var agent = new EllaiFrameworkAgent(client, agentFactory);
        EllaiDecision? decision;
        try
        {
            decision = await agent.RunAsync(context, trustedPolicy +
                "\nYou are the case history specialist, not the supervisor. Delegation is unavailable here. " +
                "Use only the authorized case timeline tool. Report only observed history and mention any incomplete page; " +
                "do not invent events, people, conclusions or actions. Return only JSON with final_decision containing " +
                "schemaVersion 2, kind Answer, displayText, spokenText, claims, questions, uiIntents, planProposal, " +
                "topicRefs, resumeSuggestion and stopReason. Each factual claim has claimId, text, kind Observed and " +
                "evidence copied exactly from the tool result: sourceKind, resourceId, version, fieldPath, evidenceId. " +
                "Use empty arrays and null for unused fields.",
                [new ChatMessage(ChatRole.User, task)], 2048, isCurrent, invoke, usage, ct, maxModelCalls: 3).ConfigureAwait(false);
        }
        finally { onModelCalls?.Invoke(agent.LastModelCallCount); }
        if (decision is null || decision.Kind != DecisionKind.Answer || decision.Claims.Count == 0 ||
            decision.UiIntents.Count != 0 || decision.PlanProposal is not null ||
            decision.Claims.Any(claim => claim.Kind != ClaimKind.Observed || claim.Evidence.Count == 0 ||
                claim.Evidence.Any(reference => reference.SourceKind != "CaseQueue")))
            return new(EllaiSpecialistOutcome.Unverified, null);
        return new(EllaiSpecialistOutcome.Completed, decision);
    }
}
