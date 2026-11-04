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
    Unverified,
    Stale
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
        CancellationToken ct, Action<int>? onModelCalls = null,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? revalidate = null)
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

        if (!EllaiSpecialistRegistry.TryGet(EllaiSpecialistRegistry.Knowledge, out var profile)) return new(EllaiSpecialistOutcome.Denied, null);
        var observations = new List<(EllaiToolRequest Request, EllaiToolResult Result)>();
        async Task<EllaiToolResult> InvokeKnowledgeTool(EllaiToolContext child, EllaiToolRequest request, CancellationToken token)
        {
            var result = await invoke(child, request, token).ConfigureAwait(false);
            if (result.Allowed) observations.Add((request, result));
            return result;
        }
        var revalidationFailed = false;
        async Task<bool> RevalidateObservations(CancellationToken token)
        {
            foreach (var (request, original) in observations)
            {
                if (!await isCurrent(token).ConfigureAwait(false)) { revalidationFailed = true; return false; }
                EllaiToolResult fresh;
                try { fresh = await (revalidate ?? invoke)(context, request, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException) { revalidationFailed = true; return false; }
                if (!fresh.Allowed || fresh.ToolName != original.ToolName || fresh.Truncated != original.Truncated ||
                    fresh.ItemCount != original.ItemCount || fresh.ResultSummary != original.ResultSummary ||
                    !fresh.Evidence.SequenceEqual(original.Evidence) || fresh.CaseTimelineReplayScope != original.CaseTimelineReplayScope ||
                    fresh.UiCatalogueReplayScope != original.UiCatalogueReplayScope || fresh.UiObservationReplayScope != original.UiObservationReplayScope)
                { revalidationFailed = true; return false; }
            }
            var current = await isCurrent(token).ConfigureAwait(false);
            if (!current) revalidationFailed = true;
            return current;
        }
        var agent = new EllaiFrameworkAgent(client, agentFactory);
        EllaiDecision? decision;
        try
        {
            decision = await agent.RunAsync(context, trustedPolicy +
                "\nYou are the knowledge specialist, not the supervisor. Delegation is unavailable here: " +
                "never call ellai_delegate_knowledge. Use approved documentation for documentation questions and any other admitted read tool when relevant to the user's SwiftAid question. " +
                "Return an answer grounded in observed evidence; do not request actions or invent sources. " +
                "Return only a JSON object with final_decision containing schemaVersion 2, kind Answer, displayText, " +
                "spokenText, claims, questions, uiIntents, planProposal, topicRefs, resumeSuggestion and stopReason. " +
                "Each factual claim must have claimId, text, kind Observed, and evidence copied exactly from a tool result. For other admitted reads, each claim text must exactly match a returned source line; join claim texts with newlines for displayText and spokenText. " +
                "When using a truncated case timeline, include its first line as a claim with the timeline evidence. " +
                "Each evidence entry has sourceKind, resourceId, version, fieldPath and evidenceId. " +
                "Use empty arrays for unused collections and null for unused optional values.",
                [new ChatMessage(ChatRole.User, task)], 2048, isCurrent, InvokeKnowledgeTool, usage, ct,
                revalidateTool: revalidate ?? invoke, revalidateStreamObservations: RevalidateObservations, maxModelCalls: profile.MaxModelCalls).ConfigureAwait(false);
        }
        finally
        {
            onModelCalls?.Invoke(agent.LastModelCallCount);
        }
        if (revalidationFailed) return new(EllaiSpecialistOutcome.Stale, null);
        if (EllaiSpecialistGrounding.IsFrameworkVerifiedDeterministic(decision, observations))
            return new(EllaiSpecialistOutcome.Completed, decision);
        if (decision is null || decision.Kind != DecisionKind.Answer || decision.Claims.Count == 0 ||
            decision.UiIntents.Count != 0 || decision.PlanProposal is not null ||
            decision.Claims.Any(claim => claim.Kind is not (ClaimKind.Observed or ClaimKind.ProductFact) ||
                claim.Evidence.Count == 0))
            return new(EllaiSpecialistOutcome.Unverified, null);
        if (!await RevalidateObservations(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Stale, null);
        if (!decision.Claims.All(claim => claim.Evidence.All(reference => reference.SourceKind == "Documentation")) &&
            !EllaiSpecialistGrounding.IsGrounded(decision, observations))
            return new(EllaiSpecialistOutcome.Unverified, null);

        return new(EllaiSpecialistOutcome.Completed, decision);
    }
}
