using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EllaiTurnToolTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "tool_trace_json",
                table: "ellai_utterances",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ellai_utterances_tool_trace_size",
                table: "ellai_utterances",
                sql: "tool_trace_json IS NULL OR octet_length(tool_trace_json::text) <= 131072");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ellai_utterances_tool_trace_size",
                table: "ellai_utterances");

            migrationBuilder.DropColumn(
                name: "tool_trace_json",
                table: "ellai_utterances");
        }
    }
}
