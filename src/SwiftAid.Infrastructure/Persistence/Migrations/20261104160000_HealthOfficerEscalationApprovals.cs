using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104160000_HealthOfficerEscalationApprovals")]
public sealed class HealthOfficerEscalationApprovals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE health_officer_escalation_approvals (
            approval_id uuid PRIMARY KEY,
            command_id uuid NOT NULL UNIQUE,
            case_id uuid NOT NULL REFERENCES managed_cases(case_id) ON DELETE RESTRICT,
            escalation_id uuid NOT NULL REFERENCES managed_case_escalations(escalation_id) ON DELETE RESTRICT,
            approver_actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
            approver_access_epoch bigint NOT NULL CHECK (approver_access_epoch >= 0),
            officer_actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
            designation_id uuid NOT NULL REFERENCES health_officer_designations(designation_id) ON DELETE RESTRICT,
            designation_version bigint NOT NULL CHECK (designation_version > 0),
            expected_case_version bigint NOT NULL CHECK (expected_case_version > 0),
            follow_up_id uuid NOT NULL,
            acknowledgement_due_utc timestamptz NOT NULL,
            follow_up_due_utc timestamptz NOT NULL CHECK (follow_up_due_utc >= acknowledgement_due_utc),
            approved_at_utc timestamptz NOT NULL,
            expires_at_utc timestamptz NOT NULL CHECK (expires_at_utc > approved_at_utc),
            UNIQUE (case_id, escalation_id, expected_case_version, officer_actor_id, designation_id, designation_version,
                follow_up_id, acknowledgement_due_utc, follow_up_due_utc)
        );
        CREATE TRIGGER immutable_health_officer_escalation_approvals BEFORE UPDATE OR DELETE ON health_officer_escalation_approvals
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM health_officer_escalation_approvals) THEN
                RAISE EXCEPTION 'Cannot remove officer escalation approvals.';
            END IF;
        END $$;
        DROP TABLE health_officer_escalation_approvals;
        """);
}
