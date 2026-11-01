using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClassificationOutboxOrderingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_outbox_ResultId",
                schema: "swiftaid_classification",
                table: "outbox",
                column: "ResultId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_ResultId",
                schema: "swiftaid_classification",
                table: "outbox");
        }
    }
}
