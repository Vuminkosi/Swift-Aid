using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EllaiEstimatedUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "estimated_completion_tokens",
                table: "ellai_usage_samples",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "estimated_dollars",
                table: "ellai_usage_samples",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "estimated_prompt_tokens",
                table: "ellai_usage_samples",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ellai_usage_estimates_nonnegative",
                table: "ellai_usage_samples",
                sql: "estimated_prompt_tokens >= 0 AND estimated_completion_tokens >= 0 AND estimated_dollars >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ellai_usage_estimates_nonnegative",
                table: "ellai_usage_samples");
            migrationBuilder.DropColumn(
                name: "estimated_completion_tokens",
                table: "ellai_usage_samples");

            migrationBuilder.DropColumn(
                name: "estimated_dollars",
                table: "ellai_usage_samples");

            migrationBuilder.DropColumn(
                name: "estimated_prompt_tokens",
                table: "ellai_usage_samples");
        }
    }
}
