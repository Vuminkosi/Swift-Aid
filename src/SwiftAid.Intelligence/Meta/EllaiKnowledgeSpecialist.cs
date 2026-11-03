using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

public enum EllaiSpecialistOutcome
{
    Completed,
    Denied,
    InvalidTask,
    Unverified
}

public sealed record EllaiSpecialistResult(EllaiSpecialistOutcome Outcome, EllaiDecision? Decision);

/// <summary>Runs one bounded knowledge task using the parent's authority and usage ledger.
/// The caller owns the supervisor response and must reauthorize before publishing it.</summary>
public sealed class EllaiKnowledgeSpecialist(IChatClient client, IEllaiFrameworkAgentFactory? agentFactory = null)
{
    public async Task<EllaiSpecialistResult> RunAsync(string task, string trustedPolicy,
        bool enabled, EllaiToolContext parent, EllaiFrameworkUsage usage,
        Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(invoke);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(task) || task.Length > 2048 || string.IsNullOrWhiteSpace(trustedPolicy))
            return new(EllaiSpecialistOutcome.InvalidTask, null);
        if (!string.Equals(parent.SessionId, usage.SessionId, StringComparison.Ordinal) ||
            !EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.Knowledge, enabled, parent, out var context) ||
            context!.AllowedTools is not { Count: > 0 })
            return new(EllaiSpecialistOutcome.Denied, null);

        context = context with { RemainingCalls = Math.Min(context.RemainingCalls, 2) };
        var agent = new EllaiFrameworkAgent(client, agentFactory);
        var decision = await agent.RunAsync(context, trustedPolicy +
            "\nYou are a bounded knowledge specialist. Use only the supplied approved documentation tools. " +
            "Return an answer grounded in observed evidence; do not request actions or invent sources.",
            [new ChatMessage(ChatRole.User, task)], 512, isCurrent, invoke, usage, ct, maxModelCalls: 3).ConfigureAwait(false);
        if (decision is null || decision.Kind != DecisionKind.Answer || decision.Claims.Count == 0 ||
            decision.UiIntents.Count != 0 || decision.PlanProposal is not null ||
            decision.Claims.Any(claim => claim.Kind is not (ClaimKind.Observed or ClaimKind.ProductFact) ||
                claim.Evidence.Count == 0 || claim.Evidence.Any(reference => reference.SourceKind != "Documentation")))
            return new(EllaiSpecialistOutcome.Unverified, null);

        return new(EllaiSpecialistOutcome.Completed, decision);
    }
}
