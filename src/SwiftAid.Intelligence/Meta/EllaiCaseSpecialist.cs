using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Bounded, read-only case queue and history specialist. The supervisor owns publication.</summary>
public sealed class EllaiCaseSpecialist(IChatClient client, IEllaiFrameworkAgentFactory? agentFactory = null)
{
    public async Task<EllaiSpecialistResult> RunAsync(string task, string trustedPolicy, bool enabled,
        EllaiToolContext parent, EllaiFrameworkUsage usage, Func<CancellationToken, Task<bool>> isCurrent,
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
        if (parent.Actor is null || !string.Equals(parent.SessionId, usage.SessionId, StringComparison.Ordinal) ||
            !EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.Case, enabled, parent, out var context) ||
            context!.AllowedTools is not { Count: > 0 })
            return new(EllaiSpecialistOutcome.Denied, null);

        if (!EllaiSpecialistRegistry.TryGet(EllaiSpecialistRegistry.Case, out var profile)) return new(EllaiSpecialistOutcome.Denied, null);
        var observations = new List<(EllaiToolRequest Request, EllaiToolResult Result)>();
        async Task<EllaiToolResult> InvokeCaseTool(EllaiToolContext child, EllaiToolRequest request, CancellationToken token)
        {
            if (request.ToolName == EllaiToolNames.State &&
                (!request.Arguments.TryGetValue("projection", out var projection) ||
                 !string.Equals(projection, "queue", StringComparison.OrdinalIgnoreCase)))
                return new(request.ToolName, false, "Only the authorized case queue is available.", [], string.Empty, 0, false,
                    EllaiToolReasonCodes.InvalidArguments);
            var result = await invoke(child, request, token).ConfigureAwait(false);
            if (result.Allowed) observations.Add((request, result));
            return result;
        }
        var agent = new EllaiFrameworkAgent(client, agentFactory);
        EllaiDecision? decision;
        try
        {
            decision = await agent.RunAsync(context, trustedPolicy +
                "\nYou are the case specialist, not the supervisor. Delegation is unavailable here. " +
                "Use the authorized case queue for owned-case searches and the case timeline for a specific case. " +
                "Report only observed cases and history, distinguish an empty result from an unavailable query, and mention incomplete pages. " +
                "Never calculate a total from a sampled case list; use an authorized aggregate for counts. " +
                "do not invent events, people, conclusions or actions. Return only JSON with final_decision containing " +
                "schemaVersion 2, kind Answer, displayText, spokenText, claims, questions, uiIntents, planProposal, " +
                "topicRefs, resumeSuggestion and stopReason. Each factual claim has claimId, text, kind Observed and " +
                "evidence copied exactly from the tool result: sourceKind, resourceId, version, fieldPath, evidenceId. " +
                "Use empty arrays and null for unused fields.",
                [new ChatMessage(ChatRole.User, task)], 2048, isCurrent, InvokeCaseTool, usage, ct,
                revalidateTool: revalidate ?? invoke, maxModelCalls: profile.MaxModelCalls, allowEmptyCaseQueue: true).ConfigureAwait(false);
        }
        finally { onModelCalls?.Invoke(agent.LastModelCallCount); }
        var verifiedEmpty = observations.Count == 1 &&
            EllaiFrameworkAgent.IsEmptyQueueObservation(observations[0].Request, observations[0].Result) &&
            decision?.DisplayText == "No matching cases were found in your current owned-case queue." &&
            decision.SpokenText == decision.DisplayText;
        if (decision is null || decision.Kind != DecisionKind.Answer || decision.Claims.Count == 0 && !verifiedEmpty ||
            decision.UiIntents.Count != 0 || decision.PlanProposal is not null ||
            decision.Claims.Any(claim => claim.Kind != ClaimKind.Observed || claim.Evidence.Count == 0 ||
                claim.Evidence.Any(reference => reference.SourceKind != "CaseQueue")))
            return new(EllaiSpecialistOutcome.Unverified, null);
        foreach (var (request, original) in observations)
        {
            if (!await isCurrent(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Denied, null);
            EllaiToolResult fresh;
            try { fresh = await (revalidate ?? invoke)(context, request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { return new(EllaiSpecialistOutcome.Stale, null); }
            if (!fresh.Allowed || fresh.ToolName != original.ToolName || fresh.Truncated != original.Truncated ||
                fresh.ItemCount != original.ItemCount || fresh.ResultSummary != original.ResultSummary ||
                !fresh.Evidence.SequenceEqual(original.Evidence) || fresh.CaseTimelineReplayScope != original.CaseTimelineReplayScope ||
                !await isCurrent(ct).ConfigureAwait(false))
                return new(EllaiSpecialistOutcome.Stale, null);
        }
        return new(EllaiSpecialistOutcome.Completed, decision);
    }
}
