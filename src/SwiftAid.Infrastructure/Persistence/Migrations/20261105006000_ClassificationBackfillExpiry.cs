using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261105006000_ClassificationBackfillExpiry")]
public sealed class ClassificationBackfillExpiry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE swiftaid_classification.backfills ADD COLUMN "Expired" bigint NOT NULL DEFAULT 0;
        ALTER TABLE swiftaid_classification.backfills DROP CONSTRAINT ck_backfill_counts;
        ALTER TABLE swiftaid_classification.backfills ADD CONSTRAINT ck_backfill_counts
            CHECK ("Eligible" = "Pending" + "NewlyClassified" + "Reused" + "Abstained" + "Failed" + "Superseded" + "Expired"
                AND LEAST("Pending", "NewlyClassified", "Reused", "Abstained", "Failed", "Superseded", "Expired") >= 0);
        ALTER TABLE swiftaid_classification.backfill_items DROP CONSTRAINT ck_backfill_item_state;
        ALTER TABLE swiftaid_classification.backfill_items ADD CONSTRAINT ck_backfill_item_state
            CHECK ("State" IN ('Pending','NewlyClassified','Reused','Abstained','Failed','Superseded','Expired'));
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN IF EXISTS (SELECT 1 FROM swiftaid_classification.backfills WHERE "Expired" <> 0) THEN
            RAISE EXCEPTION 'Cannot remove recorded backfill expiry.'; END IF; END $$;
        ALTER TABLE swiftaid_classification.backfill_items DROP CONSTRAINT ck_backfill_item_state;
        ALTER TABLE swiftaid_classification.backfill_items ADD CONSTRAINT ck_backfill_item_state
            CHECK ("State" IN ('Pending','NewlyClassified','Reused','Abstained','Failed','Superseded'));
        ALTER TABLE swiftaid_classification.backfills DROP CONSTRAINT ck_backfill_counts;
        ALTER TABLE swiftaid_classification.backfills ADD CONSTRAINT ck_backfill_counts
            CHECK ("Eligible" = "Pending" + "NewlyClassified" + "Reused" + "Abstained" + "Failed" + "Superseded"
                AND LEAST("Pending", "NewlyClassified", "Reused", "Abstained", "Failed", "Superseded") >= 0);
        ALTER TABLE swiftaid_classification.backfills DROP COLUMN "Expired";
        """);
}
