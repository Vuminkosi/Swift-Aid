using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261105005000_CommunityResearchReviews")]
public sealed class CommunityResearchReviews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE community_research_reviews (
            aggregate_id uuid PRIMARY KEY REFERENCES community_research_aggregates(aggregate_id) ON DELETE RESTRICT,
            command_id uuid NOT NULL UNIQUE,
            reviewer_actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
            decision text NOT NULL CHECK (decision IN ('Accept', 'Reject')),
            self_review_acknowledged boolean NOT NULL,
            protected_rationale bytea NOT NULL,
            recorded_at_utc timestamptz NOT NULL
        );
        CREATE TRIGGER immutable_community_research_reviews BEFORE UPDATE OR DELETE ON community_research_reviews
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM community_research_reviews) THEN RAISE EXCEPTION 'Cannot remove community research reviews.'; END IF;
        END $$;
        DROP TABLE community_research_reviews;
        """);
}
