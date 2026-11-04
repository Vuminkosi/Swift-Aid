using System.Collections.Frozen;

namespace SwiftAid.Application.Ellai.Specialists;

/// <summary>Descriptive metadata only. An entry does not enable a role or grant a tool.</summary>
public sealed record EllaiSpecialistIdentity(string AgentId, string Name, string Description, string ImplementationKind);

public static class EllaiSpecialistIdentities
{
    private static readonly FrozenDictionary<string, EllaiSpecialistIdentity> Entries = new[]
    {
        new EllaiSpecialistIdentity("core.case", "Case Intelligence", "Reads authorized case queues, timelines and possible related cases.", "interactive-partial"),
        new EllaiSpecialistIdentity("core.analytics", "Analytics", "Reads authorized owned-case counts for a requested time window.", "interactive-partial"),
        new EllaiSpecialistIdentity("core.geospatial", "Geospatial Intelligence", "Finds registered locations without inferring coordinates.", "interactive-partial"),
        new EllaiSpecialistIdentity("core.reporting", "Reporting", "Discovers entitled report controls and observed screen state.", "interactive-partial"),
        new EllaiSpecialistIdentity("core.ui", "UI Intelligence", "Discovers entitled controls and supports validated navigation guidance.", "interactive-partial"),
        new EllaiSpecialistIdentity("core.surveillance", "Surveillance Intelligence", "Reads authorized persisted classifications and candidate signals.", "interactive-partial"),
        new EllaiSpecialistIdentity("core.workflow", "Workflow", "Reads action receipts from the current Ellai session.", "interactive-partial"),
        new EllaiSpecialistIdentity("core.knowledge", "Knowledge", "Finds approved technical documentation and references.", "interactive-partial"),
        new EllaiSpecialistIdentity("ops.enrichment", "Case Enrichment", "Schedules bounded classification recovery for recent case revisions.", "operational-partial"),
        new EllaiSpecialistIdentity("ops.organization", "Case Organization", "Proposes configured queue suggestions for staff review.", "operational-partial"),
        new EllaiSpecialistIdentity("ops.followup", "Case Follow-up", "Proposes bounded follow-up and clarification work for staff review.", "operational-partial"),
        new EllaiSpecialistIdentity("ops.quality", "Data Quality", "Records deterministic report quality findings for recent cases.", "operational-partial"),
        new EllaiSpecialistIdentity("ops.spatial", "Spatial Analysis", "Reads suppressed-safe regional counts for the current officer's authorized cases.", "interactive-partial"),
        new EllaiSpecialistIdentity("ops.correlation", "Case Correlation", "Proposes unconfirmed relationships between authorized recent cases.", "operational-partial"),
        new EllaiSpecialistIdentity("community.sessions", "Community Sessions", "Reads published public sessions.", "interactive-partial"),
        new EllaiSpecialistIdentity("community.research", "Community Research", "Community feedback research is deferred.", "deferred"),
        new EllaiSpecialistIdentity("community.health-research", "Health Knowledge Research", "Lists configured, current approved source references.", "interactive-partial"),
        new EllaiSpecialistIdentity("community.education", "Educational Content", "Educational content generation is deferred.", "deferred"),
        new EllaiSpecialistIdentity("community.tips", "Tips and Prevention", "Tips and prevention generation is deferred.", "deferred")
    }.ToFrozenDictionary(item => item.AgentId, StringComparer.Ordinal);

    public static IReadOnlyCollection<EllaiSpecialistIdentity> All => Entries.Values;

    public static bool TryGet(string agentId, out EllaiSpecialistIdentity identity)
    {
        identity = null!;
        return !string.IsNullOrWhiteSpace(agentId) && Entries.TryGetValue(agentId, out identity!);
    }
}
