using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261105004000_CommunityResearchAggregates")]
public sealed class CommunityResearchAggregates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE UNIQUE INDEX ux_community_session_org ON community_sessions (session_id, organization_id);
        CREATE TABLE community_research_aggregates (
            aggregate_id uuid PRIMARY KEY,
            command_id uuid NOT NULL UNIQUE,
            organization_id text NOT NULL REFERENCES organizations(organization_id) ON DELETE RESTRICT,
            session_id uuid NOT NULL,
            session_version bigint NOT NULL CHECK (session_version > 0),
            topic_code varchar(64) NOT NULL CHECK (topic_code ~ '^[a-z][a-z0-9-]{0,63}$'),
            window_start_utc timestamptz NOT NULL,
            window_end_utc timestamptz NOT NULL CHECK (window_end_utc > window_start_utc),
            respondent_count integer NOT NULL CHECK (respondent_count BETWEEN 10 AND 1000000),
            mention_count integer NOT NULL CHECK (mention_count BETWEEN 0 AND respondent_count),
            submitted_by text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
            submitted_at_utc timestamptz NOT NULL,
            CHECK (mention_count = 0 OR mention_count >= 5),
            CHECK (mention_count = respondent_count OR respondent_count - mention_count >= 5),
            UNIQUE (session_id, session_version, topic_code),
            FOREIGN KEY (session_id, organization_id) REFERENCES community_sessions(session_id, organization_id) ON DELETE RESTRICT
        );
        CREATE INDEX ix_community_research_org_time ON community_research_aggregates (organization_id, window_end_utc DESC, aggregate_id DESC);
        CREATE TRIGGER immutable_community_research_aggregates BEFORE UPDATE OR DELETE ON community_research_aggregates
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM community_research_aggregates) THEN
                RAISE EXCEPTION 'Cannot remove submitted community research aggregates.';
            END IF;
        END $$;
        DROP TABLE community_research_aggregates;
        DROP INDEX ux_community_session_org;
        """);
}
