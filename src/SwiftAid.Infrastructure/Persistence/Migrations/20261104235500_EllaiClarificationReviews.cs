using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104235500_EllaiClarificationReviews")]
public sealed class EllaiClarificationReviews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE UNIQUE INDEX ux_ellai_clarification_proposal_case ON ellai_clarification_proposals (activity_id, case_id);
        CREATE TABLE ellai_clarification_reviews (
            recommendation_id uuid PRIMARY KEY,
            case_id uuid NOT NULL,
            command_id uuid NOT NULL UNIQUE,
            actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
            recorded_at_utc timestamptz NOT NULL,
            FOREIGN KEY (recommendation_id, case_id)
                REFERENCES ellai_clarification_proposals(activity_id, case_id) ON DELETE RESTRICT
        );
        CREATE TRIGGER immutable_ellai_clarification_reviews BEFORE UPDATE OR DELETE ON ellai_clarification_reviews
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM ellai_clarification_reviews) THEN
                RAISE EXCEPTION 'Cannot remove committed Ellai clarification reviews.';
            END IF;
        END $$;
        DROP TABLE ellai_clarification_reviews;
        DROP INDEX ux_ellai_clarification_proposal_case;
        """);
}
