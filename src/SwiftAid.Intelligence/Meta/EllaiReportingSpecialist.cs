using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Read-only report-form discovery; the supervisor and form own user-facing actions.</summary>
public sealed class EllaiReportingSpecialist(IChatClient client, IEllaiFrameworkAgentFactory? agentFactory = null)
{
    public async Task<EllaiSpecialistResult> RunAsync(string task, string trustedPolicy, EllaiToolContext parent,
        EllaiFrameworkUsage usage, Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> revalidate,
        CancellationToken ct, Action<int>? onModelCalls = null)
    {
        if (string.IsNullOrWhiteSpace(task) || task.Length > 2048 || string.IsNullOrWhiteSpace(trustedPolicy))
            return new(EllaiSpecialistOutcome.InvalidTask, null);
        if (!string.Equals(parent.SessionId, usage.SessionId, StringComparison.Ordinal) ||
            !EllaiSpecialistRegistry.TryGet(EllaiSpecialistRegistry.Reporting, out var profile) ||
            !EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.Reporting, true, parent, out var context) ||
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
                "\nYou are the reporting specialist. Use the entitled report-form catalogue and current-screen observations for report UI questions, and any other admitted read tool when relevant to the user's SwiftAid question. " +
                "Provide feature=report when querying report controls. A client observation does not establish whether a value was saved, " +
                "whether a report was submitted, or why validation did not pass. Do not invent missing values, diagnoses, " +
                "controls or actions. Return an Answer with observed claims, each exactly matching one returned source line " +
                "and citing that line's evidence. Set displayText and spokenText to the claim texts joined by a newline. " +
                "When using a truncated case timeline, include its first line as a claim with the timeline evidence. " +
                "Do not propose UI intents or actions; the supervisor and form handle guidance and explicit confirmation.",
                [new ChatMessage(ChatRole.User, task)], 2048, isCurrent, Read, usage, ct, revalidateTool: revalidate, maxModelCalls: profile.MaxModelCalls).ConfigureAwait(false);
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
            if (!fresh.Allowed || fresh.Truncated != original.Truncated ||
                original.Truncated && request.ToolName != EllaiToolNames.CaseTimeline || fresh.ToolName != original.ToolName ||
                fresh.ItemCount != original.ItemCount || fresh.ResultSummary != original.ResultSummary ||
                !fresh.Evidence.SequenceEqual(original.Evidence) || fresh.CaseTimelineReplayScope != original.CaseTimelineReplayScope ||
                (request.ToolName == EllaiToolNames.UiCatalogue && (original.UiCatalogueReplayScope is null || fresh.UiCatalogueReplayScope != original.UiCatalogueReplayScope)) ||
                (request.ToolName == EllaiToolNames.UiObserve && (original.UiObservationReplayScope is null || fresh.UiObservationReplayScope != original.UiObservationReplayScope)) ||
                !await isCurrent(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Stale, null);
        }
        if (!EllaiSpecialistGrounding.IsGrounded(decision, observations))
            return new(EllaiSpecialistOutcome.Unverified, null);
        return new(EllaiSpecialistOutcome.Completed, decision);
    }
}
