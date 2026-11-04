using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104180000_OfficerAcknowledgementRecommendations")]
public sealed class OfficerAcknowledgementRecommendations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ DECLARE constraint_name text; BEGIN
            SELECT conname INTO STRICT constraint_name FROM pg_constraint
            WHERE conrelid = 'ellai_follow_up_recommendations'::regclass AND contype = 'u'
                AND pg_get_constraintdef(oid) = 'UNIQUE (follow_up_id, schedule_version)';
            EXECUTE format('ALTER TABLE ellai_follow_up_recommendations DROP CONSTRAINT %I', constraint_name);
        END $$;
        CREATE UNIQUE INDEX ux_officer_assignment_recommendation_binding ON health_officer_escalation_assignments
            (assignment_id, case_id, follow_up_id, follow_up_event_id, aggregate_version, acknowledgement_due_utc);
        ALTER TABLE ellai_follow_up_recommendations
            ADD COLUMN recommendation_kind text NOT NULL DEFAULT 'FollowUpDue',
            ADD COLUMN officer_assignment_id uuid,
            ADD CONSTRAINT ck_follow_up_recommendation_kind CHECK (
                (recommendation_kind = 'FollowUpDue' AND officer_assignment_id IS NULL) OR
                (recommendation_kind = 'OfficerAcknowledgementDue' AND officer_assignment_id IS NOT NULL)),
            ADD CONSTRAINT fk_ack_recommendation_assignment FOREIGN KEY
                (officer_assignment_id, case_id, follow_up_id, source_event_id, schedule_version, due_at_utc)
                REFERENCES health_officer_escalation_assignments
                (assignment_id, case_id, follow_up_id, follow_up_event_id, aggregate_version, acknowledgement_due_utc)
                ON DELETE RESTRICT;
        CREATE UNIQUE INDEX ux_follow_up_due_recommendation ON ellai_follow_up_recommendations(follow_up_id, schedule_version)
            WHERE recommendation_kind = 'FollowUpDue';
        CREATE UNIQUE INDEX ux_officer_ack_recommendation ON ellai_follow_up_recommendations(officer_assignment_id)
            WHERE recommendation_kind = 'OfficerAcknowledgementDue';
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM ellai_follow_up_recommendations WHERE recommendation_kind = 'OfficerAcknowledgementDue') THEN
                RAISE EXCEPTION 'Cannot remove committed officer acknowledgment recommendations.';
            END IF;
        END $$;
        DROP INDEX ux_officer_ack_recommendation;
        DROP INDEX ux_follow_up_due_recommendation;
        ALTER TABLE ellai_follow_up_recommendations DROP CONSTRAINT fk_ack_recommendation_assignment,
            DROP CONSTRAINT ck_follow_up_recommendation_kind, DROP COLUMN officer_assignment_id, DROP COLUMN recommendation_kind;
        ALTER TABLE ellai_follow_up_recommendations ADD UNIQUE (follow_up_id, schedule_version);
        DROP INDEX ux_officer_assignment_recommendation_binding;
        """);
}
