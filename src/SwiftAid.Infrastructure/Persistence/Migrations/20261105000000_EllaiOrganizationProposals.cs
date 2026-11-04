using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261105000000_EllaiOrganizationProposals")]
public sealed class EllaiOrganizationProposals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE UNIQUE INDEX ux_ellai_organization_source_event ON case_events (event_id, case_id);
        CREATE TABLE ellai_organization_proposals (
            activity_id uuid PRIMARY KEY,
            case_id uuid NOT NULL REFERENCES managed_cases(case_id) ON DELETE RESTRICT,
            source_event_id uuid NOT NULL,
            source_case_version bigint NOT NULL CHECK (source_case_version > 0),
            source_report_revision bigint NOT NULL CHECK (source_report_revision > 0),
            queue_code varchar(100) NOT NULL CHECK (length(btrim(queue_code)) > 0),
            policy_version varchar(120) NOT NULL CHECK (length(btrim(policy_version)) > 0),
            run_id uuid NOT NULL,
            work_turn_id uuid NOT NULL UNIQUE,
            recorded_at_utc timestamptz NOT NULL,
            UNIQUE (case_id, source_case_version, queue_code, policy_version),
            FOREIGN KEY (source_event_id, case_id) REFERENCES case_events(event_id, case_id) ON DELETE RESTRICT
        );
        CREATE INDEX ix_ellai_organization_proposals_case ON ellai_organization_proposals (case_id, recorded_at_utc DESC);
        CREATE TRIGGER immutable_ellai_organization_proposals BEFORE UPDATE OR DELETE ON ellai_organization_proposals
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM ellai_organization_proposals) THEN
                RAISE EXCEPTION 'Cannot remove committed organization proposals.';
            END IF;
        END $$;
        DROP TABLE ellai_organization_proposals;
        DROP INDEX ux_ellai_organization_source_event;
        """);
}
