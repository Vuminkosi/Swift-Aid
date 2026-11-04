using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104234500_EllaiEnrichmentReuseProvenance")]
public sealed class EllaiEnrichmentReuseProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE ellai_enrichment_reuses (
            activity_id uuid PRIMARY KEY,
            job_id uuid NOT NULL REFERENCES ellai_enrichment_jobs(job_id) ON DELETE RESTRICT,
            source_event_id uuid NOT NULL,
            result_id uuid NOT NULL REFERENCES swiftaid_classification.results("Id") ON DELETE RESTRICT,
            recorded_at_utc timestamptz NOT NULL,
            UNIQUE (source_event_id, result_id),
            FOREIGN KEY (job_id, source_event_id)
                REFERENCES swiftaid_classification.backfill_items("JobId", "SourceEventId") ON DELETE RESTRICT
        );
        CREATE INDEX ix_ellai_enrichment_reuse_source ON ellai_enrichment_reuses (source_event_id, result_id);
        CREATE TRIGGER immutable_ellai_enrichment_reuses BEFORE UPDATE OR DELETE ON ellai_enrichment_reuses
            FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM ellai_enrichment_reuses) THEN
                RAISE EXCEPTION 'Cannot remove committed enrichment reuse provenance.';
            END IF;
        END $$;
        DROP TABLE ellai_enrichment_reuses;
        """);
}
