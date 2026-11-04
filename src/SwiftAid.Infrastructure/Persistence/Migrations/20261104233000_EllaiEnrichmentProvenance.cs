using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104233000_EllaiEnrichmentProvenance")]
public sealed class EllaiEnrichmentProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE ellai_enrichment_jobs (
            job_id uuid PRIMARY KEY REFERENCES swiftaid_classification.backfills("Id") ON DELETE RESTRICT,
            run_id uuid NOT NULL,
            work_turn_id uuid NOT NULL UNIQUE,
            policy_version text NOT NULL CHECK (length(policy_version) BETWEEN 1 AND 120)
        );
        CREATE TABLE ellai_enrichment_claims (
            claim_id uuid PRIMARY KEY,
            job_id uuid NOT NULL REFERENCES ellai_enrichment_jobs(job_id) ON DELETE RESTRICT,
            source_event_id uuid NOT NULL,
            result_id uuid NOT NULL REFERENCES swiftaid_classification.results("Id") ON DELETE RESTRICT,
            lease_id uuid NOT NULL,
            recorded_at_utc timestamptz NOT NULL,
            UNIQUE (job_id, source_event_id, lease_id),
            FOREIGN KEY (job_id, source_event_id)
                REFERENCES swiftaid_classification.backfill_items("JobId", "SourceEventId") ON DELETE RESTRICT
        );
        CREATE INDEX ix_ellai_enrichment_claim_source ON ellai_enrichment_claims (source_event_id, result_id);
        CREATE TRIGGER immutable_ellai_enrichment_jobs BEFORE UPDATE OR DELETE ON ellai_enrichment_jobs
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        CREATE TRIGGER immutable_ellai_enrichment_claims BEFORE UPDATE OR DELETE ON ellai_enrichment_claims
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM ellai_enrichment_jobs) OR EXISTS (SELECT 1 FROM ellai_enrichment_claims) THEN
                RAISE EXCEPTION 'Cannot remove committed enrichment provenance.';
            END IF;
        END $$;
        DROP TABLE ellai_enrichment_claims;
        DROP TABLE ellai_enrichment_jobs;
        """);
}
