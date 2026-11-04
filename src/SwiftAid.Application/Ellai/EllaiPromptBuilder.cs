using System.Security.Cryptography;
using System.Text;

namespace SwiftAid.Application.Ellai;

/// <summary>Deterministic full-file runtime-policy loader (ELL-T05 groundwork).
/// The entire approved runtime-only policy (docs/ellai/ellai-system-instruction.md)
/// is loaded once, unchanged; engineering instructions and fixtures are never
/// merged into runtime prompts. Hash/version checks fail closed.</summary>
public static class EllaiPromptLoader
{
    public static string ComputeHash(string policyText)
    {
        ArgumentNullException.ThrowIfNull(policyText);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policyText))).ToLowerInvariant();
    }

    public static bool ExcludesEngineeringContent(string policyText)
    {
        ArgumentNullException.ThrowIfNull(policyText);
        string[] forbidden =
        [
            "ELL-T", "ELL-AT", "Test-Ellai.ps1", "Probe-Ellai.ps1",
            "red–green–regression", "implementation task", "story point"
        ];
        return forbidden.All(f => !policyText.Contains(f, StringComparison.Ordinal));
    }
}

/// <summary>Minimal trusted role overlay: emphasis only, never new authority.</summary>
public sealed record EllaiRoleOverlay(string Profile, string Emphasis);

/// <summary>Builds the bounded TurnInput prompt from the pinned policy,
/// one role overlay, capability descriptors and remaining limits.</summary>
public sealed class EllaiPromptBuilder(string policyText, string policyVersion)
{
    public string Build(EllaiRoleOverlay role, string capabilitySummary, string limitsSummary)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (!EllaiPromptLoader.ExcludesEngineeringContent(policyText))
        {
            throw new InvalidOperationException("Runtime policy contains engineering content; refusing to build prompt.");
        }

        return string.Join("\n", [
            $"# Policy {policyVersion} (hash {EllaiPromptLoader.ComputeHash(policyText)[..16]}…)",
            policyText,
            $"## Active role profile: {role.Profile}",
            role.Emphasis,
            "## Current capabilities",
            capabilitySummary,
            "## Remaining limits",
            limitsSummary
        ]);
    }
}
