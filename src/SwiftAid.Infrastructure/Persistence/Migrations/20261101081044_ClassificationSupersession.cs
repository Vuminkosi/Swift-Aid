using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClassificationSupersession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_ResultId",
                schema: "swiftaid_classification",
                table: "outbox");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_ResultId_CaseId_ReportRevision_EventType",
                schema: "swiftaid_classification",
                table: "outbox",
                columns: new[] { "ResultId", "CaseId", "ReportRevision", "EventType" },
                unique: true,
                filter: "\"EventType\" = 'ClassificationSuperseded'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_ResultId_CaseId_ReportRevision_EventType",
                schema: "swiftaid_classification",
                table: "outbox");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_ResultId",
                schema: "swiftaid_classification",
                table: "outbox",
                column: "ResultId");
        }
    }
}
