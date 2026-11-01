using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClassificationBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backfills",
                schema: "swiftaid_classification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<string>(type: "text", nullable: false),
                    WorkspaceId = table.Column<string>(type: "text", nullable: false),
                    OrganizationId = table.Column<string>(type: "text", nullable: false),
                    ProfileJson = table.Column<string>(type: "jsonb", nullable: false),
                    ConfigurationHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Eligible = table.Column<long>(type: "bigint", nullable: false),
                    Pending = table.Column<long>(type: "bigint", nullable: false),
                    NewlyClassified = table.Column<long>(type: "bigint", nullable: false),
                    Reused = table.Column<long>(type: "bigint", nullable: false),
                    Abstained = table.Column<long>(type: "bigint", nullable: false),
                    Failed = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backfills", x => x.Id);
                    table.CheckConstraint("ck_backfill_counts", "\"Eligible\" = \"Pending\" + \"NewlyClassified\" + \"Reused\" + \"Abstained\" + \"Failed\" AND LEAST(\"Pending\", \"NewlyClassified\", \"Reused\", \"Abstained\", \"Failed\") >= 0");
                    table.CheckConstraint("ck_backfill_state", "\"State\" IN ('Running','Paused','Completed')");
                    table.ForeignKey(
                        name: "FK_backfills_workspace_organizations_WorkspaceId_OrganizationId",
                        columns: x => new { x.WorkspaceId, x.OrganizationId },
                        principalTable: "workspace_organizations",
                        principalColumns: new[] { "workspace_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backfill_items",
                schema: "swiftaid_classification",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    ResultId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backfill_items", x => new { x.JobId, x.SourceEventId });
                    table.CheckConstraint("ck_backfill_item_state", "\"State\" IN ('Pending','NewlyClassified','Reused','Abstained','Failed')");
                    table.ForeignKey(
                        name: "FK_backfill_items_backfills_JobId",
                        column: x => x.JobId,
                        principalSchema: "swiftaid_classification",
                        principalTable: "backfills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backfill_items_case_events_SourceEventId",
                        column: x => x.SourceEventId,
                        principalTable: "case_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backfill_items_results_ResultId",
                        column: x => x.ResultId,
                        principalSchema: "swiftaid_classification",
                        principalTable: "results",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backfill_items_JobId_Ordinal",
                schema: "swiftaid_classification",
                table: "backfill_items",
                columns: new[] { "JobId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backfill_items_ResultId",
                schema: "swiftaid_classification",
                table: "backfill_items",
                column: "ResultId");

            migrationBuilder.CreateIndex(
                name: "IX_backfill_items_SourceEventId",
                schema: "swiftaid_classification",
                table: "backfill_items",
                column: "SourceEventId");

            migrationBuilder.CreateIndex(
                name: "ix_backfill_pending",
                schema: "swiftaid_classification",
                table: "backfill_items",
                columns: new[] { "JobId", "Ordinal" },
                filter: "\"State\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_backfills_WorkspaceId_OrganizationId",
                schema: "swiftaid_classification",
                table: "backfills",
                columns: new[] { "WorkspaceId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backfill_items",
                schema: "swiftaid_classification");

            migrationBuilder.DropTable(
                name: "backfills",
                schema: "swiftaid_classification");
        }
    }
}
