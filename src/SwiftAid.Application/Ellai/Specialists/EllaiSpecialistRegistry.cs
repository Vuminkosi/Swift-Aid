using System.Collections.Frozen;

namespace SwiftAid.Application.Ellai.Specialists;

/// <summary>A server-owned profile. A profile does not itself grant access to any tool.</summary>
public sealed record EllaiSpecialistProfile(string AgentId, int Version, string FeatureFlag,
    IReadOnlySet<string> ToolAllowlist);

/// <summary>Initial registry for bounded, read-only specialist work.</summary>
public static class EllaiSpecialistRegistry
{
    public const string Knowledge = "core.knowledge";
    public const string Case = "core.case";
    public const string Analytics = "core.analytics";

    private static readonly FrozenDictionary<string, EllaiSpecialistProfile> Profiles =
        new[]
        {
            new EllaiSpecialistProfile(Knowledge, 1, "Ellai:Specialists:Knowledge:Enabled",
                new[] { EllaiToolNames.DocsSearch, EllaiToolNames.DocsGet }.ToFrozenSet(StringComparer.Ordinal)),
            new EllaiSpecialistProfile(Case, 1, "Ellai:Specialists:Case:Enabled",
                new[] { EllaiToolNames.CaseTimeline }.ToFrozenSet(StringComparer.Ordinal)),
            new EllaiSpecialistProfile(Analytics, 1, "Ellai:Specialists:Analytics:Enabled",
                new[] { EllaiToolNames.OwnedCaseCount }.ToFrozenSet(StringComparer.Ordinal))
        }.ToFrozenDictionary(profile => profile.AgentId, StringComparer.Ordinal);

    public static bool TryGet(string agentId, out EllaiSpecialistProfile profile)
    {
        profile = null!;
        return !string.IsNullOrWhiteSpace(agentId) && Profiles.TryGetValue(agentId, out profile!);
    }

    /// <summary>Restricts an already authorized context; the caller must obtain
    /// the feature flag and parent context from trusted server state.</summary>
    public static bool TryCreateToolContext(string agentId, bool enabled, EllaiToolContext parent,
        out EllaiToolContext? specialist)
    {
        ArgumentNullException.ThrowIfNull(parent);
        specialist = null;
        if (!enabled || !TryGet(agentId, out var profile) || parent.AllowedTools is null)
            return false;

        var allowed = profile.ToolAllowlist.Where(parent.AllowedTools.Contains)
            .Where(EllaiToolContractCatalog.All.ContainsKey)
            .ToFrozenSet(StringComparer.Ordinal);
        specialist = parent with { AllowedTools = allowed };
        return true;
    }
}
