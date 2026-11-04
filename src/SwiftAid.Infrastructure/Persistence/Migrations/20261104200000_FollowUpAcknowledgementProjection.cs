using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104200000_FollowUpAcknowledgementProjection")]
public sealed class FollowUpAcknowledgementProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "acknowledged_version", table: "managed_case_follow_ups",
            type: "bigint", nullable: true);
        migrationBuilder.AddCheckConstraint(name: "ck_managed_case_follow_ups_ack_version", table: "managed_case_follow_ups",
            sql: "acknowledged_version IS NULL OR acknowledged_version >= 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM managed_case_follow_ups WHERE acknowledged_version IS NOT NULL) THEN
                    RAISE EXCEPTION 'Cannot remove populated follow-up acknowledgement projection.';
                END IF;
            END $$;
            """);
        migrationBuilder.DropCheckConstraint(name: "ck_managed_case_follow_ups_ack_version", table: "managed_case_follow_ups");
        migrationBuilder.DropColumn(name: "acknowledged_version", table: "managed_case_follow_ups");
    }
}
