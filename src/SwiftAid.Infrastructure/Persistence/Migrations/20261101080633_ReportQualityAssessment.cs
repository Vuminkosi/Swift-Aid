using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportQualityAssessment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quality_assessments",
                schema: "swiftaid_classification",
                columns: table => new
                {
                    SourceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportRevision = table.Column<long>(type: "bigint", nullable: false),
                    AssessmentJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quality_assessments", x => new { x.SourceEventId, x.Version });
                    table.ForeignKey(
                        name: "FK_quality_assessments_case_events_SourceEventId",
                        column: x => x.SourceEventId,
                        principalTable: "case_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quality_assessments_managed_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quality_assessments_CaseId_ReportRevision",
                schema: "swiftaid_classification",
                table: "quality_assessments",
                columns: new[] { "CaseId", "ReportRevision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quality_assessments",
                schema: "swiftaid_classification");
        }
    }
}
