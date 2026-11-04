using Microsoft.Extensions.AI;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Read-only semantic UI discovery; the supervisor owns user-facing output and actions.</summary>
public sealed class EllaiUiSpecialist(IChatClient client, IEllaiFrameworkAgentFactory? agentFactory = null)
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
            !EllaiSpecialistRegistry.TryGet(EllaiSpecialistRegistry.Ui, out var profile) ||
            !EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.Ui, true, parent, out var context) ||
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
                "\nYou are the UI discovery specialist. Use only the entitled semantic catalogue and current-screen observations. " +
                "Do not invent controls, routes, screen state, navigation, tour steps or completed actions. " +
                "Return only a grounded Answer in the required final_decision JSON schema; include exact observed evidence. " +
                "Do not propose UI intents or actions; the supervisor handles validated guidance.",
                [new ChatMessage(ChatRole.User, task)], 2048, isCurrent, Read, usage, ct, maxModelCalls: profile.MaxModelCalls).ConfigureAwait(false);
        }
        finally { onModelCalls?.Invoke(agent.LastModelCallCount); }
        if (decision is null || decision.Kind != DecisionKind.Answer || decision.Claims.Count == 0 ||
            decision.UiIntents.Count != 0 || decision.PlanProposal is not null ||
            decision.Claims.Any(claim => claim.Kind != ClaimKind.Observed || claim.Evidence.Count == 0 ||
                claim.Evidence.Any(e => e.SourceKind is not ("UiCatalogue" or "UiObservation"))))
            return new(EllaiSpecialistOutcome.Unverified, null);
        foreach (var (request, original) in observations)
        {
            if (!await isCurrent(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Stale, null);
            EllaiToolResult fresh;
            try { fresh = await revalidate(context, request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { return new(EllaiSpecialistOutcome.Stale, null); }
            if (!fresh.Allowed || original.Truncated || fresh.Truncated || fresh.ToolName != original.ToolName ||
                !fresh.Evidence.SequenceEqual(original.Evidence) ||
                (request.ToolName == EllaiToolNames.UiCatalogue && (original.UiCatalogueReplayScope is null || fresh.UiCatalogueReplayScope != original.UiCatalogueReplayScope)) ||
                (request.ToolName == EllaiToolNames.UiObserve && (original.UiObservationReplayScope is null || fresh.UiObservationReplayScope != original.UiObservationReplayScope)) ||
                !await isCurrent(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Stale, null);
        }
        return new(EllaiSpecialistOutcome.Completed, decision);
    }
}
