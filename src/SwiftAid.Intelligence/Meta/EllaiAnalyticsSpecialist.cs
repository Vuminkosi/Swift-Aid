using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Bounded analytics over an authorized aggregate. The supervisor owns publication.</summary>
public sealed class EllaiAnalyticsSpecialist(IChatClient client, IEllaiFrameworkAgentFactory? agentFactory = null)
{
    public async Task<EllaiSpecialistResult> RunAsync(string task, string trustedPolicy, bool enabled,
        EllaiToolContext parent, EllaiFrameworkUsage usage, Func<CancellationToken, Task<bool>> isCurrent,
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
        if (parent.Actor is null || !string.Equals(parent.SessionId, usage.SessionId, StringComparison.Ordinal) ||
            !EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.Analytics, enabled, parent, out var context) ||
            context!.AllowedTools is not { Count: > 0 })
            return new(EllaiSpecialistOutcome.Denied, null);

        if (!EllaiSpecialistRegistry.TryGet(EllaiSpecialistRegistry.Analytics, out var profile)) return new(EllaiSpecialistOutcome.Denied, null);
        var observations = new List<(EllaiToolRequest Request, EllaiToolResult Result)>();
        var deniedObservation = false;
        async Task<EllaiToolResult> Read(EllaiToolContext child, EllaiToolRequest request, CancellationToken token)
        {
            var result = await invoke(child, request, token).ConfigureAwait(false);
            if (result.Allowed) observations.Add((request, result));
            else if (result.ReasonCode != EllaiToolReasonCodes.InvalidArguments) deniedObservation = true;
            return result;
        }
        var agent = new EllaiFrameworkAgent(client, agentFactory);
        EllaiDecision? decision;
        try
        {
            decision = await agent.RunAsync(context, trustedPolicy +
                "\nYou are the analytics specialist, not the supervisor. Delegation is unavailable here. " +
                "Use the authorized owned-case count tool for count questions with an explicitly requested 1 to 365 day window, and any other admitted read tool when relevant to the user's SwiftAid question. " +
                "Do not infer a window, calculate counts from samples, infer incidence, or invent evidence. " +
                "If the count window is missing, ask for it. For other reads, each claim must exactly match a returned source line and cite its evidence; join claim texts with newlines for displayText and spokenText. " +
                "When using a truncated case timeline, include its first line as a claim with the timeline evidence.",
                [new ChatMessage(ChatRole.User, task)], 1024, isCurrent, Read, usage, ct,
                revalidateTool: revalidate, revalidateStreamObservations: token =>
                    EllaiSpecialistGrounding.RevalidateAsync(context, observations, deniedObservation, isCurrent, revalidate, token),
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
        if (!EllaiSpecialistGrounding.IsGrounded(decision, observations)) return new(EllaiSpecialistOutcome.Unverified, null);
        return new(EllaiSpecialistOutcome.Completed, decision);
    }
}
