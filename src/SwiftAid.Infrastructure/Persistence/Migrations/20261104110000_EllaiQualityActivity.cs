using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104110000_EllaiQualityActivity")]
public sealed class EllaiQualityActivity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX ux_quality_case_source_version
                ON swiftaid_classification.quality_assessments ("CaseId", "SourceEventId", "Version");
            CREATE TABLE ellai_case_quality_activity (
                activity_id uuid PRIMARY KEY,
                case_id uuid NOT NULL,
                source_event_id uuid NOT NULL,
                quality_version varchar(100) NOT NULL,
                role_id text NOT NULL CHECK (role_id = 'ops.quality'),
                agent_version text NOT NULL CHECK (agent_version = '1'),
                policy_version text NOT NULL CHECK (length(policy_version) BETWEEN 1 AND 120),
                operation_id uuid NOT NULL UNIQUE,
                run_id uuid NOT NULL,
                work_turn_id uuid NOT NULL UNIQUE,
                source_case_version bigint NOT NULL CHECK (source_case_version > 0),
                input_fingerprint char(64) NOT NULL CHECK (input_fingerprint ~ '^[0-9a-f]{64}$'),
                recorded_at_utc timestamptz NOT NULL,
                UNIQUE (case_id, source_event_id, quality_version, role_id),
                FOREIGN KEY (case_id, source_event_id, quality_version)
                    REFERENCES swiftaid_classification.quality_assessments ("CaseId", "SourceEventId", "Version")
                    ON DELETE RESTRICT
            );
            CREATE INDEX ix_ellai_quality_activity_source ON ellai_case_quality_activity (case_id, source_event_id, recorded_at_utc);
            CREATE TRIGGER immutable_ellai_quality_activity BEFORE UPDATE OR DELETE ON ellai_case_quality_activity
                FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM ellai_case_quality_activity) THEN
                    RAISE EXCEPTION 'Cannot remove committed Ellai case activity.';
                END IF;
            END $$;
            DROP TABLE ellai_case_quality_activity;
            DROP INDEX swiftaid_classification.ux_quality_case_source_version;
            """);
    }
}
