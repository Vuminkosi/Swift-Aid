using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104235000_EllaiClarificationProposals")]
public sealed class EllaiClarificationProposals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE ellai_clarification_proposals (
            activity_id uuid PRIMARY KEY,
            case_id uuid NOT NULL,
            source_event_id uuid NOT NULL,
            quality_version varchar(100) NOT NULL,
            finding_code text NOT NULL CHECK (finding_code IN
                ('occurrence-not-known', 'region-not-known', 'incomplete-coordinate-pair', 'text-evidence-unavailable')),
            source_case_version bigint NOT NULL CHECK (source_case_version > 0),
            policy_version varchar(120) NOT NULL CHECK (length(btrim(policy_version)) > 0),
            run_id uuid NOT NULL,
            work_turn_id uuid NOT NULL UNIQUE,
            recorded_at_utc timestamptz NOT NULL,
            UNIQUE (source_event_id, finding_code),
            FOREIGN KEY (case_id, source_event_id, quality_version)
                REFERENCES swiftaid_classification.quality_assessments ("CaseId", "SourceEventId", "Version") ON DELETE RESTRICT
        );
        CREATE INDEX ix_ellai_clarification_source ON ellai_clarification_proposals (case_id, source_event_id, recorded_at_utc);
        CREATE TRIGGER immutable_ellai_clarification_proposals BEFORE UPDATE OR DELETE ON ellai_clarification_proposals
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM ellai_clarification_proposals) THEN
                RAISE EXCEPTION 'Cannot remove committed clarification proposals.';
            END IF;
        END $$;
        DROP TABLE ellai_clarification_proposals;
        """);
}
