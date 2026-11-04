using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Reads designated officers with one complete current case-scoped grant.
/// This lookup does not grant access, validate professional licensure, or assign an officer.</summary>
public sealed class PostgresEligibleHealthOfficerStore(SwiftAidDbContext db) : IEligibleHealthOfficerStore
{
    public async Task<EligibleHealthOfficerPage> GetAsync(Guid caseId, int limit, CancellationToken ct)
    {
        if (caseId == Guid.Empty || limit is < 1 or > 20) throw new ArgumentException("A case and bounded limit are required.");
        var take = limit + 1;
        var rows = await db.Database.SqlQuery<OfficerRow>($"""
            SELECT DISTINCT ON (officer.actor_id) officer.designation_id AS "DesignationId",
                officer.version AS "DesignationVersion", officer.actor_id AS "ActorId", profile.display_name AS "DisplayLabel"
            FROM managed_cases c
            JOIN organizations org ON org.organization_id = c.organization_id AND org.status = 'Active'
            JOIN workspaces ws ON ws.workspace_id = c.workspace_id AND ws.status = 'Active'
            JOIN workspace_organizations link ON link.workspace_id = c.workspace_id AND link.organization_id = c.organization_id AND link.status = 'Active'
            JOIN health_officer_designations officer ON officer.organization_id = c.organization_id
                AND officer.country_code = c.country_code AND officer.sector = c.sector AND officer.status = 'Active'
                AND officer.valid_from_utc <= clock_timestamp() AND officer.expires_at_utc > clock_timestamp()
                AND NOT EXISTS (SELECT 1 FROM health_officer_designations newer
                    WHERE newer.actor_id = officer.actor_id AND newer.organization_id = officer.organization_id
                        AND newer.country_code = officer.country_code AND newer.sector = officer.sector
                        AND newer.version > officer.version)
            JOIN actors actor ON actor.actor_id = officer.actor_id AND actor.status = 'Active'
                AND (actor.expires_at_utc IS NULL OR actor.expires_at_utc > clock_timestamp())
            JOIN actor_access_states state ON state.actor_id = actor.actor_id
            JOIN organization_memberships member ON member.actor_id = actor.actor_id AND member.organization_id = c.organization_id
                AND member.status = 'Active' AND member.valid_from_utc <= clock_timestamp()
                AND (member.expires_at_utc IS NULL OR member.expires_at_utc > clock_timestamp())
            JOIN access_grant_groups grant_group ON grant_group.actor_id = actor.actor_id AND grant_group.membership_id = member.membership_id
                AND grant_group.organization_id = c.organization_id AND grant_group.workspace_id = c.workspace_id
                AND grant_group.country_code = c.country_code AND grant_group.sector = c.sector
                AND grant_group.sensitivity_code = c.sensitivity_code AND grant_group.status = 'Active'
                AND grant_group.valid_from_utc <= clock_timestamp() AND grant_group.expires_at_utc > clock_timestamp()
                AND grant_group.can_access_jurisdictional_cases
            LEFT JOIN swiftaid_identity.user_profiles profile ON profile.user_id = actor.actor_id
            WHERE c.case_id = {caseId} AND c.workflow <> 'Resolved'
                AND EXISTS (SELECT 1 FROM managed_case_regions region WHERE region.case_id = c.case_id)
                AND NOT EXISTS (SELECT 1 FROM managed_case_regions region WHERE region.case_id = c.case_id
                    AND NOT EXISTS (SELECT 1 FROM access_grant_group_regions coverage
                        WHERE coverage.grant_group_id = grant_group.grant_group_id AND coverage.region_id = region.region_id))
                AND NOT EXISTS (SELECT 1 FROM unnest(ARRAY['ReadCase', 'ManageEscalation', 'ManageFollowUp']::text[]) required(operation)
                    WHERE NOT EXISTS (SELECT 1 FROM access_grant_group_operations allowed
                        WHERE allowed.grant_group_id = grant_group.grant_group_id AND allowed.operation = required.operation))
            ORDER BY officer.actor_id, officer.designation_id LIMIT {take}
            """).ToArrayAsync(ct).ConfigureAwait(false);
        return new(rows.Take(limit).Select(row => new EligibleHealthOfficer(row.DesignationId, row.DesignationVersion,
            row.ActorId, row.DisplayLabel is { Length: > 0 and <= 120 } label && !label.Any(char.IsControl) ? label : null)).ToArray(), rows.Length > limit);
    }

    private sealed class OfficerRow
    {
        public Guid DesignationId { get; init; }
        public long DesignationVersion { get; init; }
        public string ActorId { get; init; } = "";
        public string? DisplayLabel { get; init; }
    }
}
