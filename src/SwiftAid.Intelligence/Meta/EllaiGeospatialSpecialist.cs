using Microsoft.Extensions.AI;
using System.Text.Json;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Bounded registry place lookup. Spatial analysis and case mapping are not provided.</summary>
public sealed class EllaiGeospatialSpecialist(IChatClient client, IEllaiFrameworkAgentFactory? agentFactory = null)
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
            !EllaiSpecialistRegistry.TryGet(EllaiSpecialistRegistry.Geospatial, out var profile) ||
            !EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.Geospatial, true, parent, out var context) ||
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
                "\nYou are the geographic registry specialist. Use registry-backed place matches for location questions and any other admitted read tool when relevant to the user's SwiftAid question. " +
                "Never invent coordinates, boundaries, case locations, spatial analysis or population figures. " +
                "Place matches are candidates; ask the user to choose when ambiguous. " +
                "Return only a grounded Answer in the required final_decision JSON schema. " +
                "For place matches each claim must be exactly a returned place name; for other reads each claim must exactly match a returned source line. Cite the matching evidence. " +
                "For place-only answers join claim texts by a comma and one space; otherwise join them by a newline. Set displayText and spokenText to that text, with no other prose. " +
                "When using a truncated case timeline, include its first line as a claim with the timeline evidence. " +
                "When a search has multiple matches, include a clarifying question asking the user to select a place.",
                [new ChatMessage(ChatRole.User, task)], 2048, isCurrent, Read, usage, ct,
                revalidateTool: revalidate, maxModelCalls: profile.MaxModelCalls).ConfigureAwait(false);
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
                fresh.UiCatalogueReplayScope != original.UiCatalogueReplayScope || fresh.UiObservationReplayScope != original.UiObservationReplayScope ||
                !await isCurrent(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Stale, null);
        }
        // References alone do not prove that generated prose describes the referenced
        // location. Permit only exact registry names bound to their own evidence.
        var grounded = new HashSet<(EvidenceRef Reference, string Name)>();
        try
        {
            foreach (var (request, result) in observations.Where(item => item.Request.ToolName == EllaiToolNames.Locations))
            {
                using var json = JsonDocument.Parse(result.ResultSummary);
                foreach (var item in json.RootElement.GetProperty("matches").EnumerateArray())
                {
                    var id = item.GetProperty("Id").GetString();
                    var name = item.GetProperty("Name").GetString();
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                        return new(EllaiSpecialistOutcome.Unverified, null);
                    foreach (var reference in result.Evidence.Where(reference => reference.ResourceId == id))
                        grounded.Add((reference, name));
                }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new(EllaiSpecialistOutcome.Unverified, null); }
        foreach (var (request, result) in observations.Where(item => item.Request.ToolName != EllaiToolNames.Locations))
            foreach (var reference in result.Evidence)
                foreach (var line in EllaiFrameworkAgent.EvidenceLines(request.ToolName, result, reference))
                    grounded.Add((reference, line));
        var placeOnly = decision.Claims.All(claim => claim.Evidence.All(reference => reference.SourceKind == "GeographicRegistry"));
        if (!EllaiSpecialistGrounding.MentionsTimelineOmissions(decision, observations) ||
            decision.Claims.Any(claim => claim.Evidence.Any(reference => !grounded.Contains((reference, claim.Text)))) ||
            decision.DisplayText != string.Join(placeOnly ? ", " : "\n", decision.Claims.Select(claim => claim.Text)) ||
            decision.SpokenText != decision.DisplayText ||
            observations.Any(item => item.Request.ToolName == EllaiToolNames.Locations && item.Result.ItemCount > 1) && decision.Questions.Count == 0)
            return new(EllaiSpecialistOutcome.Unverified, null);
        return new(EllaiSpecialistOutcome.Completed, decision);
    }
}
