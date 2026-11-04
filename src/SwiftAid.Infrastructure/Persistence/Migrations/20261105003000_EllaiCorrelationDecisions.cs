using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261105003000_EllaiCorrelationDecisions")]
public sealed class EllaiCorrelationDecisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE ellai_correlation_decisions (
            candidate_id uuid PRIMARY KEY REFERENCES ellai_correlation_candidates(candidate_id) ON DELETE RESTRICT,
            command_id uuid NOT NULL UNIQUE,
            actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
            decision text NOT NULL CHECK (decision IN ('Accept', 'Reject')),
            protected_rationale bytea NOT NULL,
            recorded_at_utc timestamptz NOT NULL
        );
        CREATE TRIGGER immutable_ellai_correlation_decisions BEFORE UPDATE OR DELETE ON ellai_correlation_decisions
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM ellai_correlation_decisions) THEN
                RAISE EXCEPTION 'Cannot remove committed correlation decisions.';
            END IF;
        END $$;
        DROP TABLE ellai_correlation_decisions;
        """);
}
