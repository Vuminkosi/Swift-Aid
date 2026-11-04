using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104120000_EllaiFollowUpRecommendations")]
public sealed class EllaiFollowUpRecommendations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX ux_follow_up_case_identity ON managed_case_follow_ups (follow_up_id, case_id);
            CREATE UNIQUE INDEX ux_case_event_case_identity ON case_events (event_id, case_id);
            CREATE TABLE ellai_follow_up_recommendations (
                activity_id uuid PRIMARY KEY,
                case_id uuid NOT NULL REFERENCES managed_cases(case_id) ON DELETE RESTRICT,
                follow_up_id uuid NOT NULL,
                source_event_id uuid NOT NULL,
                schedule_version bigint NOT NULL CHECK (schedule_version > 0),
                due_at_utc timestamptz NOT NULL,
                source_case_version bigint NOT NULL CHECK (source_case_version > 0),
                policy_version varchar(120) NOT NULL CHECK (length(btrim(policy_version)) > 0),
                run_id uuid NOT NULL,
                work_turn_id uuid NOT NULL UNIQUE,
                recorded_at_utc timestamptz NOT NULL,
                UNIQUE (follow_up_id, schedule_version),
                FOREIGN KEY (follow_up_id, case_id) REFERENCES managed_case_follow_ups(follow_up_id, case_id) ON DELETE RESTRICT,
                FOREIGN KEY (source_event_id, case_id) REFERENCES case_events(event_id, case_id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_ellai_follow_up_recommendations_source
                ON ellai_follow_up_recommendations (case_id, source_event_id, recorded_at_utc);
            CREATE TRIGGER immutable_ellai_follow_up_recommendations BEFORE UPDATE OR DELETE ON ellai_follow_up_recommendations
                FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM ellai_follow_up_recommendations) THEN
                    RAISE EXCEPTION 'Cannot remove committed Ellai follow-up recommendations.';
                END IF;
            END $$;
            DROP TABLE ellai_follow_up_recommendations;
            DROP INDEX ux_case_event_case_identity;
            DROP INDEX ux_follow_up_case_identity;
            """);
    }
}
