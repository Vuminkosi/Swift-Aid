using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104170000_HealthOfficerEscalationAssignments")]
public sealed class HealthOfficerEscalationAssignments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE UNIQUE INDEX ux_officer_escalation_case ON managed_case_escalations (escalation_id, case_id);
        CREATE UNIQUE INDEX ux_officer_designation_actor_version ON health_officer_designations (designation_id, actor_id, version);
        ALTER TABLE health_officer_escalation_approvals ADD CONSTRAINT fk_officer_approval_escalation_case
            FOREIGN KEY (escalation_id, case_id) REFERENCES managed_case_escalations(escalation_id, case_id) ON DELETE RESTRICT;
        ALTER TABLE health_officer_escalation_approvals ADD CONSTRAINT fk_officer_approval_designation_actor_version
            FOREIGN KEY (designation_id, officer_actor_id, designation_version)
            REFERENCES health_officer_designations(designation_id, actor_id, version) ON DELETE RESTRICT;
        CREATE UNIQUE INDEX ux_officer_approval_assignment_binding ON health_officer_escalation_approvals
            (approval_id, case_id, escalation_id, follow_up_id, officer_actor_id, approver_actor_id, acknowledgement_due_utc);
        CREATE UNIQUE INDEX ux_officer_follow_up_case ON managed_case_follow_ups (follow_up_id, case_id);
        CREATE UNIQUE INDEX ux_officer_event_case_type_version ON case_events (event_id, case_id, event_type, aggregate_version);
        CREATE TABLE health_officer_escalation_assignments (
            assignment_id uuid PRIMARY KEY,
            approval_id uuid NOT NULL UNIQUE REFERENCES health_officer_escalation_approvals(approval_id) ON DELETE RESTRICT,
            execution_command_id uuid NOT NULL UNIQUE,
            case_id uuid NOT NULL REFERENCES managed_cases(case_id) ON DELETE RESTRICT,
            escalation_id uuid NOT NULL UNIQUE REFERENCES managed_case_escalations(escalation_id) ON DELETE RESTRICT,
            follow_up_id uuid NOT NULL UNIQUE REFERENCES managed_case_follow_ups(follow_up_id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED,
            follow_up_event_id uuid NOT NULL UNIQUE REFERENCES case_events(event_id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED,
            officer_actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
            assigned_by_actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
            acknowledgement_due_utc timestamptz NOT NULL,
            assigned_at_utc timestamptz NOT NULL,
            aggregate_version bigint NOT NULL CHECK (aggregate_version > 0),
            follow_up_event_type text NOT NULL DEFAULT 'CaseFollowUpScheduled' CHECK (follow_up_event_type = 'CaseFollowUpScheduled'),
            FOREIGN KEY (approval_id, case_id, escalation_id, follow_up_id, officer_actor_id, assigned_by_actor_id, acknowledgement_due_utc)
                REFERENCES health_officer_escalation_approvals
                (approval_id, case_id, escalation_id, follow_up_id, officer_actor_id, approver_actor_id, acknowledgement_due_utc) ON DELETE RESTRICT,
            FOREIGN KEY (follow_up_id, case_id) REFERENCES managed_case_follow_ups(follow_up_id, case_id)
                ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED,
            FOREIGN KEY (follow_up_event_id, case_id, follow_up_event_type, aggregate_version)
                REFERENCES case_events(event_id, case_id, event_type, aggregate_version)
                ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
        );
        CREATE INDEX ix_health_officer_escalation_assignment_case ON health_officer_escalation_assignments(case_id);
        CREATE TRIGGER immutable_health_officer_escalation_assignments BEFORE UPDATE OR DELETE ON health_officer_escalation_assignments
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        CREATE FUNCTION swiftaid_classification.verify_officer_assignment_at_commit() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM health_officer_escalation_approvals a
                JOIN health_officer_designations d ON d.designation_id = a.designation_id AND d.version = a.designation_version
                WHERE a.approval_id = NEW.approval_id AND a.expires_at_utc > clock_timestamp()
                    AND a.acknowledgement_due_utc > clock_timestamp() AND d.status = 'Active'
                    AND d.valid_from_utc <= clock_timestamp() AND d.expires_at_utc > clock_timestamp()
                    AND NOT EXISTS (SELECT 1 FROM health_officer_designations newer
                        WHERE newer.actor_id = d.actor_id AND newer.organization_id = d.organization_id
                            AND newer.country_code = d.country_code AND newer.sector = d.sector AND newer.version > d.version)
            ) THEN RAISE EXCEPTION 'The officer assignment authority has expired or changed.'; END IF;
            RETURN NEW;
        END $$;
        CREATE CONSTRAINT TRIGGER verify_officer_assignment_at_commit AFTER INSERT ON health_officer_escalation_assignments
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.verify_officer_assignment_at_commit();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM health_officer_escalation_assignments) THEN
                RAISE EXCEPTION 'Cannot remove officer escalation assignments.';
            END IF;
        END $$;
        DROP TABLE health_officer_escalation_assignments;
        DROP FUNCTION swiftaid_classification.verify_officer_assignment_at_commit();
        DROP INDEX ux_officer_event_case_type_version;
        DROP INDEX ux_officer_follow_up_case;
        DROP INDEX ux_officer_approval_assignment_binding;
        ALTER TABLE health_officer_escalation_approvals DROP CONSTRAINT fk_officer_approval_designation_actor_version;
        ALTER TABLE health_officer_escalation_approvals DROP CONSTRAINT fk_officer_approval_escalation_case;
        DROP INDEX ux_officer_designation_actor_version;
        DROP INDEX ux_officer_escalation_case;
        """);
}
