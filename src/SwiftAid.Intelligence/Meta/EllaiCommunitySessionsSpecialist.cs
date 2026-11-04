using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Reads published public sessions; it cannot schedule or execute actions.</summary>
public sealed class EllaiCommunitySessionsSpecialist(IChatClient client, IEllaiFrameworkAgentFactory? agentFactory = null)
{
    public async Task<EllaiSpecialistResult> RunAsync(string task, string trustedPolicy, EllaiToolContext parent,
        EllaiFrameworkUsage usage, Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> revalidate,
        CancellationToken ct, Action<int>? onModelCalls = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(invoke);
        ArgumentNullException.ThrowIfNull(revalidate);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(task) || task.Length > 2048 || string.IsNullOrWhiteSpace(trustedPolicy))
            return new(EllaiSpecialistOutcome.InvalidTask, null);
        if (!string.Equals(parent.SessionId, usage.SessionId, StringComparison.Ordinal) ||
            !EllaiSpecialistRegistry.TryGet(EllaiSpecialistRegistry.CommunitySessions, out var profile) ||
            !EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.CommunitySessions, true, parent, out var context) ||
            context!.AllowedTools is not { Count: > 0 }) return new(EllaiSpecialistOutcome.Denied, null);

        var observations = new List<(EllaiToolRequest Request, EllaiToolResult Result)>();
        async Task<EllaiToolResult> Read(EllaiToolContext child, EllaiToolRequest request, CancellationToken token)
        {
            var result = await invoke(child, request, token).ConfigureAwait(false);
            if (result.Allowed) observations.Add((request, result));
            return result;
        }

        var agent = new EllaiFrameworkAgent(client, agentFactory);
        EllaiDecision? decision;
        try
        {
            decision = await agent.RunAsync(context, trustedPolicy +
                "\nYou are the community sessions specialist. Read published public sessions and use any other admitted read tool when relevant to the user's SwiftAid question. Apply a session status filter only when the user requests it. " +
                "Never approve, submit, " +
                "execute or invent an action. Return only a grounded Answer in the required final_decision JSON schema. " +
                "Each claim text must exactly match a returned source line and cite its evidence. For an empty session search, use the exact returned search message and cite its PublicSessionSearch evidence. " +
                "Set displayText and spokenText to claim texts joined by a newline, with no other prose.",
                [new ChatMessage(ChatRole.User, task)], 2048, isCurrent, Read, usage, ct,
                maxModelCalls: profile.MaxModelCalls).ConfigureAwait(false);
        }
        finally { onModelCalls?.Invoke(agent.LastModelCallCount); }
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
                !await isCurrent(ct).ConfigureAwait(false))
                return new(EllaiSpecialistOutcome.Stale, null);
        }
        if (!EllaiSpecialistGrounding.IsGrounded(decision, observations))
            return new(EllaiSpecialistOutcome.Unverified, null);
        return new(EllaiSpecialistOutcome.Completed, decision);
    }
}
