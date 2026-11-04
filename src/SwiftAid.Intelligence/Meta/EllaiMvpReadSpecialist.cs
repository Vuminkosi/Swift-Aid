using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>General MVP read path for a specialist using the parent session's authority.</summary>
internal sealed class EllaiMvpReadSpecialist(IChatClient client, IEllaiFrameworkAgentFactory? agentFactory = null)
{
    public async Task<EllaiSpecialistResult> RunAsync(string agentId, string task, string trustedPolicy,
        EllaiToolContext parent, EllaiFrameworkUsage usage, Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> revalidate,
        CancellationToken ct, Action<int>? onModelCalls = null)
    {
        if (string.IsNullOrWhiteSpace(task) || task.Length > 2048 || string.IsNullOrWhiteSpace(trustedPolicy))
            return new(EllaiSpecialistOutcome.InvalidTask, null);
        if (!string.Equals(parent.SessionId, usage.SessionId, StringComparison.Ordinal) ||
            !EllaiSpecialistRegistry.TryGet(agentId, out var profile) ||
            !EllaiSpecialistRegistry.TryCreateToolContext(agentId, true, parent, out var context) ||
            context!.AllowedTools is not { Count: > 0 }) return new(EllaiSpecialistOutcome.Denied, null);
        var observations = new List<(EllaiToolRequest Request, EllaiToolResult Result)>();
        var deniedObservation = false;
        async Task<EllaiToolResult> Read(EllaiToolContext child, EllaiToolRequest request, CancellationToken token)
        {
            var result = await invoke(child, request, token).ConfigureAwait(false);
            if (result.Allowed) observations.Add((request, result));
            else if (result.ReasonCode != EllaiToolReasonCodes.InvalidArguments) deniedObservation = true;
            return result;
        }
        async Task<bool> RevalidateObservations(CancellationToken token)
        {
            if (deniedObservation) return false;
            foreach (var (request, original) in observations)
            {
                if (!await isCurrent(token).ConfigureAwait(false)) return false;
                EllaiToolResult fresh;
                try { fresh = await revalidate(context, request, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException) { return false; }
                if (!fresh.Allowed || fresh.Truncated != original.Truncated || fresh.ToolName != original.ToolName ||
                    fresh.ItemCount != original.ItemCount || fresh.ResultSummary != original.ResultSummary ||
                    !fresh.Evidence.SequenceEqual(original.Evidence) || fresh.CaseTimelineReplayScope != original.CaseTimelineReplayScope ||
                    fresh.UiCatalogueReplayScope != original.UiCatalogueReplayScope || fresh.UiObservationReplayScope != original.UiObservationReplayScope)
                    return false;
            }
            return await isCurrent(token).ConfigureAwait(false);
        }
        var agent = new EllaiFrameworkAgent(client, agentFactory);
        EllaiDecision? decision;
        try
        {
            decision = await agent.RunAsync(context, trustedPolicy +
                "\nYou are a SwiftAid specialist. Use any admitted read tool relevant to the question. " +
                "Do not invent facts, execute actions, diagnose or provide medical advice. Return an Answer with observed claims; " +
                "each claim must exactly match a returned source line and cite its evidence. " +
                "When using a truncated case timeline, include its first line as a claim with the timeline evidence. " +
                "Set displayText and spokenText to claim texts joined by newlines.",
                [new ChatMessage(ChatRole.User, task)], 2048, isCurrent, Read, usage, ct,
                revalidateTool: revalidate, revalidateStreamObservations: RevalidateObservations,
                maxModelCalls: profile.MaxModelCalls).ConfigureAwait(false);
        }
        finally { onModelCalls?.Invoke(agent.LastModelCallCount); }
        if (EllaiSpecialistGrounding.IsFrameworkVerifiedDeterministic(decision, observations))
            return new(EllaiSpecialistOutcome.Completed, decision);
        if (decision is null || decision.Kind != DecisionKind.Answer || decision.Claims.Count == 0 ||
            decision.UiIntents.Count != 0 || decision.PlanProposal is not null ||
            decision.Claims.Any(claim => claim.Kind != ClaimKind.Observed || claim.Evidence.Count == 0))
            return new(EllaiSpecialistOutcome.Unverified, null);
        foreach (var (request, original) in observations)
        {
            if (!await isCurrent(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Stale, null);
            EllaiToolResult fresh;
            try { fresh = await revalidate(context, request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { return new(EllaiSpecialistOutcome.Stale, null); }
            if (!fresh.Allowed || fresh.Truncated != original.Truncated || fresh.ToolName != original.ToolName ||
                fresh.ItemCount != original.ItemCount || fresh.ResultSummary != original.ResultSummary ||
                !fresh.Evidence.SequenceEqual(original.Evidence) || fresh.CaseTimelineReplayScope != original.CaseTimelineReplayScope ||
                fresh.UiCatalogueReplayScope != original.UiCatalogueReplayScope || fresh.UiObservationReplayScope != original.UiObservationReplayScope ||
                !await isCurrent(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Stale, null);
        }
        return EllaiSpecialistGrounding.IsGrounded(decision, observations)
            ? new(EllaiSpecialistOutcome.Completed, decision) : new(EllaiSpecialistOutcome.Unverified, null);
    }
}
