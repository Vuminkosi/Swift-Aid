using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EllaiGuidanceCheckpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "guidance_json",
                table: "ellai_sessions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ellai_sessions_guidance_size",
                table: "ellai_sessions",
                sql: "guidance_json IS NULL OR octet_length(guidance_json::text) <= 131072");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ellai_sessions_guidance_size",
                table: "ellai_sessions");

            migrationBuilder.DropColumn(
                name: "guidance_json",
                table: "ellai_sessions");
        }
    }
}
