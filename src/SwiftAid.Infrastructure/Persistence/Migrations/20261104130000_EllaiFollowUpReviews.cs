using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104130000_EllaiFollowUpReviews")]
public sealed class EllaiFollowUpReviews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX ux_ellai_follow_up_recommendation_case
                ON ellai_follow_up_recommendations (activity_id, case_id);
            CREATE TABLE ellai_follow_up_reviews (
                recommendation_id uuid PRIMARY KEY,
                case_id uuid NOT NULL,
                command_id uuid NOT NULL UNIQUE,
                actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
                recorded_at_utc timestamptz NOT NULL,
                FOREIGN KEY (recommendation_id, case_id)
                    REFERENCES ellai_follow_up_recommendations(activity_id, case_id) ON DELETE RESTRICT
            );
            CREATE TRIGGER immutable_ellai_follow_up_reviews BEFORE UPDATE OR DELETE ON ellai_follow_up_reviews
                FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM ellai_follow_up_reviews) THEN
                    RAISE EXCEPTION 'Cannot remove committed Ellai follow-up reviews.';
                END IF;
            END $$;
            DROP TABLE ellai_follow_up_reviews;
            DROP INDEX ux_ellai_follow_up_recommendation_case;
            """);
    }
}
