using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104220000_ClassificationBackfillSuperseded")]
public sealed class ClassificationBackfillSuperseded : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE swiftaid_classification.backfills ADD COLUMN "Superseded" bigint NOT NULL DEFAULT 0;
            ALTER TABLE swiftaid_classification.backfills DROP CONSTRAINT ck_backfill_counts;
            ALTER TABLE swiftaid_classification.backfills ADD CONSTRAINT ck_backfill_counts
                CHECK ("Eligible" = "Pending" + "NewlyClassified" + "Reused" + "Abstained" + "Failed" + "Superseded"
                    AND LEAST("Pending", "NewlyClassified", "Reused", "Abstained", "Failed", "Superseded") >= 0);
            ALTER TABLE swiftaid_classification.backfill_items DROP CONSTRAINT ck_backfill_item_state;
            ALTER TABLE swiftaid_classification.backfill_items ADD CONSTRAINT ck_backfill_item_state
                CHECK ("State" IN ('Pending','NewlyClassified','Reused','Abstained','Failed','Superseded'));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM swiftaid_classification.backfills WHERE "Superseded" <> 0)
                    OR EXISTS (SELECT 1 FROM swiftaid_classification.backfill_items WHERE "State" = 'Superseded') THEN
                    RAISE EXCEPTION 'Cannot remove recorded superseded backfill outcomes.';
                END IF;
            END $$;
            ALTER TABLE swiftaid_classification.backfill_items DROP CONSTRAINT ck_backfill_item_state;
            ALTER TABLE swiftaid_classification.backfill_items ADD CONSTRAINT ck_backfill_item_state
                CHECK ("State" IN ('Pending','NewlyClassified','Reused','Abstained','Failed'));
            ALTER TABLE swiftaid_classification.backfills DROP CONSTRAINT ck_backfill_counts;
            ALTER TABLE swiftaid_classification.backfills DROP COLUMN "Superseded";
            ALTER TABLE swiftaid_classification.backfills ADD CONSTRAINT ck_backfill_counts
                CHECK ("Eligible" = "Pending" + "NewlyClassified" + "Reused" + "Abstained" + "Failed"
                    AND LEAST("Pending", "NewlyClassified", "Reused", "Abstained", "Failed") >= 0);
            """);
    }
}
