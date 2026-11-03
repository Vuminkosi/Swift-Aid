using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104100000_EllaiOperationalSchedules")]
public sealed class EllaiOperationalSchedules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE ellai_operational_schedules (
                workspace_id text NOT NULL REFERENCES workspaces(workspace_id),
                role_id text NOT NULL,
                cadence_hours integer NOT NULL CHECK (cadence_hours IN (4, 8)),
                policy_version text NOT NULL CHECK (length(policy_version) BETWEEN 1 AND 120),
                next_due_utc timestamptz NOT NULL,
                lease_id uuid NULL,
                lease_started_utc timestamptz NULL,
                lease_until_utc timestamptz NULL,
                fencing_token bigint NOT NULL DEFAULT 0 CHECK (fencing_token >= 0),
                PRIMARY KEY (workspace_id, role_id),
                CHECK (role_id IN ('ops.enrichment', 'ops.quality', 'ops.followup', 'ops.organization', 'ops.correlation', 'ops.spatial')),
                CHECK ((lease_id IS NULL) = (lease_until_utc IS NULL)),
                CHECK ((lease_id IS NULL) = (lease_started_utc IS NULL))
            );
            CREATE INDEX ix_ellai_operational_schedules_due ON ellai_operational_schedules (next_due_utc, workspace_id, role_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM ellai_operational_schedules) THEN
                    RAISE EXCEPTION 'Cannot remove operational schedules while schedule state exists.';
                END IF;
            END $$;
            DROP TABLE ellai_operational_schedules;
            """);
    }
}
