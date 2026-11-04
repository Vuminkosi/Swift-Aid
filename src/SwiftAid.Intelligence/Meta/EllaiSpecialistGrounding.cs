using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Checks that a read-only specialist answer is built from observed result lines.</summary>
internal static class EllaiSpecialistGrounding
{
    public static async Task<bool> RevalidateAsync(EllaiToolContext context,
        IReadOnlyList<(EllaiToolRequest Request, EllaiToolResult Result)> observations, bool deniedObservation,
        Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> revalidate,
        CancellationToken ct)
    {
        if (deniedObservation) return false;
        foreach (var (request, original) in observations)
        {
            if (!await isCurrent(ct).ConfigureAwait(false)) return false;
            EllaiToolResult fresh;
            try { fresh = await revalidate(context, request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { return false; }
            if (!fresh.Allowed || fresh.ToolName != original.ToolName || fresh.Truncated != original.Truncated ||
                fresh.ItemCount != original.ItemCount || fresh.ResultSummary != original.ResultSummary ||
                !fresh.Evidence.SequenceEqual(original.Evidence) || fresh.CaseTimelineReplayScope != original.CaseTimelineReplayScope ||
                fresh.UiCatalogueReplayScope != original.UiCatalogueReplayScope || fresh.UiObservationReplayScope != original.UiObservationReplayScope)
                return false;
        }
        return await isCurrent(ct).ConfigureAwait(false);
    }

    // These results are built and revalidated by EllaiFrameworkAgent itself;
    // their fresh evidence can differ from the original tool observation.
    public static bool IsFrameworkVerifiedDeterministic(EllaiDecision? decision,
        IReadOnlyList<(EllaiToolRequest Request, EllaiToolResult Result)> observations)
    {
        if (decision is not { Kind: DecisionKind.Answer, Claims.Count: 1 } || observations.Count != 1 ||
            decision.UiIntents.Count != 0 || decision.PlanProposal is not null || decision.Claims[0].Evidence.Count != 1)
            return false;
        var expected = observations[0].Request.ToolName switch
        {
            EllaiToolNames.OwnedCaseCount => "DashboardAggregate",
            EllaiToolNames.OwnedRegionCounts => "SpatialAggregate",
            EllaiToolNames.ApprovedHealthSources => "HealthSourceRegistry",
            _ => null
        };
        return expected is not null && decision.Claims[0].Kind == ClaimKind.Observed &&
            decision.Claims[0].Evidence[0].SourceKind == expected &&
            decision.Claims[0].Text == decision.DisplayText && decision.SpokenText == decision.DisplayText;
    }

    public static bool IsGrounded(EllaiDecision decision, IEnumerable<(EllaiToolRequest Request, EllaiToolResult Result)> observations)
    {
        var observed = observations.ToArray();
        var lines = new HashSet<(EvidenceRef Reference, string Line)>();
        foreach (var (request, result) in observed)
            foreach (var reference in result.Evidence)
                foreach (var line in EllaiFrameworkAgent.EvidenceLines(request.ToolName, result, reference))
                    lines.Add((reference, line));
        return MentionsTimelineOmissions(decision, observed) && decision.Claims.All(claim => claim.Evidence.All(reference => lines.Contains((reference, claim.Text)))) &&
            decision.DisplayText == string.Join("\n", decision.Claims.Select(claim => claim.Text)) &&
            decision.SpokenText == decision.DisplayText;
    }

    public static bool MentionsTimelineOmissions(EllaiDecision decision,
        IEnumerable<(EllaiToolRequest Request, EllaiToolResult Result)> observations) =>
        observations.Where(item => item.Request.ToolName == EllaiToolNames.CaseTimeline && item.Result.Truncated)
            .All(item => !decision.Claims.Any(claim => claim.Evidence.Any(item.Result.Evidence.Contains)) ||
                decision.Claims.Any(claim => claim.Text == item.Result.ResultSummary.Split('\n')[0] &&
                    claim.Evidence.Any(item.Result.Evidence.Contains)));
}
