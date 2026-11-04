using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BoundedCaseShareOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "allowed_operations",
                table: "case_shares",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_shares_operations",
                table: "case_shares",
                sql: "allowed_operations IS NULL OR (array_position(allowed_operations, NULL) IS NULL AND allowed_operations @> ARRAY['ReadCase']::text[] AND allowed_operations <@ ARRAY['ReadCase','ReadAttachment','Synchronize','RecordObservation','ManageFollowUp','ManageEscalation']::text[])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM case_shares WHERE allowed_operations IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove bounded share operations while bounded shares exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_case_shares_operations",
                table: "case_shares");

            migrationBuilder.DropColumn(
                name: "allowed_operations",
                table: "case_shares");
        }
    }
}
