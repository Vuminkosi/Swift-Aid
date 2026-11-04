using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Checks that a read-only specialist answer is built from observed result lines.</summary>
internal static class EllaiSpecialistGrounding
{
    public static bool IsGrounded(EllaiDecision decision, IEnumerable<(EllaiToolRequest Request, EllaiToolResult Result)> observations)
    {
        var lines = new HashSet<(EvidenceRef Reference, string Line)>();
        foreach (var (request, result) in observations)
            foreach (var reference in result.Evidence)
                foreach (var line in EllaiFrameworkAgent.EvidenceLines(request.ToolName, result, reference))
                    lines.Add((reference, line));
        return decision.Claims.All(claim => claim.Evidence.All(reference => lines.Contains((reference, claim.Text)))) &&
            decision.DisplayText == string.Join("\n", decision.Claims.Select(claim => claim.Text)) &&
            decision.SpokenText == decision.DisplayText;
    }
}
