using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EllaiUsageSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ellai_usage_samples",
                columns: table => new
                {
                    sample_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    completion_tokens = table.Column<int>(type: "integer", nullable: false),
                    dollars = table.Column<decimal>(type: "numeric", nullable: false),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ellai_usage_samples", x => x.sample_id);
                    table.CheckConstraint("ck_ellai_usage_samples_nonnegative", "prompt_tokens >= 0 AND completion_tokens >= 0 AND dollars >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_ellai_usage_samples_session",
                table: "ellai_usage_samples",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ellai_usage_samples");
        }
    }
}
