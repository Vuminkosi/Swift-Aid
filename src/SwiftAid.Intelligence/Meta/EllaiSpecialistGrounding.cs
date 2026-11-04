using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Checks that a read-only specialist answer is built from observed result lines.</summary>
internal static class EllaiSpecialistGrounding
{
    public static bool IsGrounded(EllaiDecision decision, IEnumerable<(EllaiToolRequest Request, EllaiToolResult Result)> observations)
    {
        var observed = observations.ToArray();
        var lines = new HashSet<(EvidenceRef Reference, string Line)>();
        foreach (var (request, result) in observed)
            foreach (var reference in result.Evidence)
                foreach (var line in EllaiFrameworkAgent.EvidenceLines(request.ToolName, result, reference))
                    lines.Add((reference, line));
        var mentionsOmissions = observed.Where(item => item.Request.ToolName == EllaiToolNames.CaseTimeline && item.Result.Truncated)
            .All(item => !decision.Claims.Any(claim => claim.Evidence.Any(item.Result.Evidence.Contains)) ||
                decision.Claims.Any(claim => claim.Text == item.Result.ResultSummary.Split('\n')[0] &&
                    claim.Evidence.Any(item.Result.Evidence.Contains)));
        return mentionsOmissions && decision.Claims.All(claim => claim.Evidence.All(reference => lines.Contains((reference, claim.Text)))) &&
            decision.DisplayText == string.Join("\n", decision.Claims.Select(claim => claim.Text)) &&
            decision.SpokenText == decision.DisplayText;
    }
}
