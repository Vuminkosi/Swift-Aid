using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261105002000_EllaiCorrelationCandidates")]
public sealed class EllaiCorrelationCandidates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE ellai_correlation_candidates (
            candidate_id uuid PRIMARY KEY,
            workspace_id text NOT NULL REFERENCES workspaces(workspace_id) ON DELETE RESTRICT,
            organization_id text NOT NULL REFERENCES organizations(organization_id) ON DELETE RESTRICT,
            first_case_id uuid NOT NULL REFERENCES managed_cases(case_id) ON DELETE RESTRICT,
            second_case_id uuid NOT NULL REFERENCES managed_cases(case_id) ON DELETE RESTRICT,
            first_report_revision bigint NOT NULL CHECK (first_report_revision > 0),
            second_report_revision bigint NOT NULL CHECK (second_report_revision > 0),
            first_event_id uuid NOT NULL,
            second_event_id uuid NOT NULL,
            reason_codes text[] NOT NULL CHECK (cardinality(reason_codes) > 0),
            policy_version varchar(120) NOT NULL,
            run_id uuid NOT NULL,
            work_turn_id uuid NOT NULL UNIQUE,
            recorded_at_utc timestamptz NOT NULL,
            CHECK (first_case_id < second_case_id),
            UNIQUE (first_case_id, second_case_id, first_report_revision, second_report_revision, policy_version),
            FOREIGN KEY (first_event_id, first_case_id) REFERENCES case_events(event_id, case_id) ON DELETE RESTRICT,
            FOREIGN KEY (second_event_id, second_case_id) REFERENCES case_events(event_id, case_id) ON DELETE RESTRICT
        );
        CREATE INDEX ix_ellai_correlation_first_case ON ellai_correlation_candidates(first_case_id, recorded_at_utc DESC);
        CREATE INDEX ix_ellai_correlation_second_case ON ellai_correlation_candidates(second_case_id, recorded_at_utc DESC);
        CREATE TRIGGER immutable_ellai_correlation_candidates BEFORE UPDATE OR DELETE ON ellai_correlation_candidates
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM ellai_correlation_candidates) THEN
                RAISE EXCEPTION 'Cannot remove committed correlation candidates.';
            END IF;
        END $$;
        DROP TABLE ellai_correlation_candidates;
        """);
}
