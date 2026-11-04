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
                "\nYou are the geographic registry specialist. Use only registry-backed place matches. " +
                "Never invent coordinates, boundaries, case locations, spatial analysis or population figures. " +
                "Place matches are candidates; ask the user to choose when ambiguous. " +
                "Return only a grounded Answer in the required final_decision JSON schema. " +
                "Each claim text must be exactly one returned place name and cite its matching registry evidence. " +
                "Set displayText and spokenText to the claim texts joined by a comma and one space, with no other prose. " +
                "When a search has multiple matches, include a clarifying question asking the user to select a place.",
                [new ChatMessage(ChatRole.User, task)], 2048, isCurrent, Read, usage, ct,
                maxModelCalls: profile.MaxModelCalls).ConfigureAwait(false);
        }
        finally { onModelCalls?.Invoke(agent.LastModelCallCount); }
        if (decision is null || decision.Kind != DecisionKind.Answer || decision.Claims.Count == 0 ||
            decision.UiIntents.Count != 0 || decision.PlanProposal is not null ||
            decision.Claims.Any(claim => claim.Kind != ClaimKind.Observed || claim.Evidence.Count == 0 ||
                claim.Evidence.Any(reference => reference.SourceKind != "GeographicRegistry")))
            return new(EllaiSpecialistOutcome.Unverified, null);
        foreach (var (request, original) in observations)
        {
            if (!await isCurrent(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Stale, null);
            EllaiToolResult fresh;
            try { fresh = await revalidate(context, request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { return new(EllaiSpecialistOutcome.Stale, null); }
            if (!fresh.Allowed || original.Truncated || fresh.Truncated || fresh.ToolName != original.ToolName ||
                fresh.ItemCount != original.ItemCount || fresh.ResultSummary != original.ResultSummary ||
                !fresh.Evidence.SequenceEqual(original.Evidence) ||
                !await isCurrent(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Stale, null);
        }
        // References alone do not prove that generated prose describes the referenced
        // location. Permit only exact registry names bound to their own evidence.
        var grounded = new HashSet<(EvidenceRef Reference, string Name)>();
        try
        {
            foreach (var (_, result) in observations)
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
        if (decision.Claims.Any(claim => claim.Evidence.Any(reference => !grounded.Contains((reference, claim.Text)))) ||
            decision.DisplayText != string.Join(", ", decision.Claims.Select(claim => claim.Text)) ||
            decision.SpokenText != decision.DisplayText ||
            observations.Any(item => item.Result.ItemCount > 1) && decision.Questions.Count == 0)
            return new(EllaiSpecialistOutcome.Unverified, null);
        return new(EllaiSpecialistOutcome.Completed, decision);
    }
}
