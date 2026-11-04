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
            if (!request.Arguments.TryGetValue("feature", out var feature) || feature != EllaiReportUiManifest.FeatureId)
                return new(request.ToolName, false, "Only the report form is available.", [], string.Empty, 0, false,
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
                "\nYou are the reporting specialist. Read only the entitled report-form catalogue and current-screen observations. " +
                "Always provide feature=report to both tools. A client observation does not establish whether a value was saved, " +
                "whether a report was submitted, or why validation did not pass. Do not invent missing values, diagnoses, " +
                "controls or actions. Return an Answer with observed claims, each exactly matching one returned control line " +
                "and citing that line's evidence. Set displayText and spokenText to the claim texts joined by a newline. " +
                "Do not propose UI intents or actions; the supervisor and form handle guidance and explicit confirmation.",
                [new ChatMessage(ChatRole.User, task)], 2048, isCurrent, Read, usage, ct, maxModelCalls: profile.MaxModelCalls).ConfigureAwait(false);
        }
        finally { onModelCalls?.Invoke(agent.LastModelCallCount); }
        if (decision is null || decision.Kind != DecisionKind.Answer || decision.Claims.Count == 0 ||
            decision.UiIntents.Count != 0 || decision.PlanProposal is not null ||
            decision.Claims.Any(claim => claim.Kind != ClaimKind.Observed || claim.Evidence.Count == 0 ||
                claim.Evidence.Any(e => e.SourceKind is not ("UiCatalogue" or "UiObservation"))))
            return new(EllaiSpecialistOutcome.Unverified, null);
        var grounded = new HashSet<(EvidenceRef Reference, string Line)>();
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
            foreach (var reference in original.Evidence)
                foreach (var line in original.ResultSummary.Split('\n'))
                    if (line.StartsWith($"control {reference.ResourceId} · ", StringComparison.Ordinal))
                        grounded.Add((reference, line));
        }
        if (decision.Claims.Any(claim => claim.Evidence.Any(reference => !grounded.Contains((reference, claim.Text)))) ||
            decision.DisplayText != string.Join("\n", decision.Claims.Select(claim => claim.Text)) ||
            decision.SpokenText != decision.DisplayText)
            return new(EllaiSpecialistOutcome.Unverified, null);
        return new(EllaiSpecialistOutcome.Completed, decision);
    }
}
