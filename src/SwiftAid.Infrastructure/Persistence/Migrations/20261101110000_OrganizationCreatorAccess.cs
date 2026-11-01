using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

/// <summary>Data-only backfill of proven creators; never restores previously administered grants.</summary>
[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261101110000_OrganizationCreatorAccess")]
public sealed class OrganizationCreatorAccess : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            WITH inserted AS (
                INSERT INTO access_grant_groups
                    (grant_group_id, membership_id, actor_id, organization_id, workspace_id, country_code,
                     sector, sensitivity_code, can_access_jurisdictional_cases, status, valid_from_utc,
                     expires_at_utc, granted_by_actor_id, change_reason, version)
                SELECT 'creator-' || replace(m.membership_id::text, '-', '') || '-' || c.code || '-' || s.sector,
                    m.membership_id, m.actor_id, m.organization_id, w.workspace_id, c.code, s.sector,
                    'standard', true, 'Active', CURRENT_TIMESTAMP,
                    COALESCE(m.expires_at_utc, 'infinity'::timestamptz), m.actor_id, 'OrganizationCreator', 1
                FROM organization_memberships m
                JOIN organizations o ON o.organization_id = m.organization_id AND o.status = 'Active'
                JOIN workspaces w ON w.organization_id = o.organization_id
                    AND w.workspace_id = o.organization_id || '-general' AND w.status = 'Active'
                JOIN workspace_organizations link ON link.organization_id = o.organization_id
                    AND link.workspace_id = w.workspace_id AND link.status = 'Active'
                JOIN actors a ON a.actor_id = m.actor_id AND a.status = 'Active'
                CROSS JOIN (VALUES ('ZW'), ('SZ')) c(code)
                CROSS JOIN (VALUES ('HumanHealth'), ('AnimalHealth')) s(sector)
                WHERE m.status = 'Active' AND m.actor_id = m.granted_by_actor_id
                    AND m.valid_from_utc = o.created_at_utc AND m.valid_from_utc <= CURRENT_TIMESTAMP
                    AND (m.expires_at_utc IS NULL OR m.expires_at_utc > CURRENT_TIMESTAMP)
                    AND (a.expires_at_utc IS NULL OR a.expires_at_utc > CURRENT_TIMESTAMP)
                    AND EXISTS (SELECT 1 FROM security_audit_entries audit
                        WHERE audit.actor_id = m.actor_id AND audit.operation = 'Organization.Created'
                          AND audit.decision_code = 'Allowed' AND audit.recorded_at_utc = o.created_at_utc)
                    AND NOT EXISTS (SELECT 1 FROM access_grant_groups prior
                        WHERE prior.membership_id = m.membership_id AND prior.workspace_id = w.workspace_id)
                ON CONFLICT DO NOTHING
                RETURNING grant_group_id, actor_id
            ), operations AS (
                INSERT INTO access_grant_group_operations (grant_group_id, operation)
                SELECT grant_group_id, operation FROM inserted
                CROSS JOIN (VALUES ('ReadCase'), ('Search'), ('Count')) op(operation)
                RETURNING grant_group_id
            )
            INSERT INTO security_audit_entries
                (audit_id, actor_id, operation, decision_code, access_epoch, recorded_at_utc, reason_code)
            SELECT gen_random_uuid(), i.actor_id, 'Organization.CreatorAccessBackfilled', 'Allowed',
                state.access_epoch, CURRENT_TIMESTAMP, i.grant_group_id
            FROM inserted i JOIN actor_access_states state ON state.actor_id = i.actor_id;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Creator grants may own accepted cases. Revoke through controlled administration; do not downgrade.");
}
