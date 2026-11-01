using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistentClassificationRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "swiftaid_classification");

            migrationBuilder.CreateTable(
                name: "configurations",
                schema: "swiftaid_classification",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProfileJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "results",
                schema: "swiftaid_classification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrganizationId = table.Column<string>(type: "text", nullable: false),
                    WorkspaceId = table.Column<string>(type: "text", nullable: false),
                    ConfigurationId = table.Column<string>(type: "character varying(64)", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkerId = table.Column<string>(type: "text", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    PredictionsJson = table.Column<string>(type: "jsonb", nullable: true),
                    AbstentionReason = table.Column<string>(type: "text", nullable: true),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    InferenceMilliseconds = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_results", x => x.Id);
                    table.CheckConstraint("ck_classification_attempts", "\"Attempts\" BETWEEN 0 AND 8");
                    table.CheckConstraint("ck_classification_lease", "(\"State\" = 'Leased') = (\"LeaseId\" IS NOT NULL AND \"LeaseUntilUtc\" IS NOT NULL AND \"WorkerId\" IS NOT NULL)");
                    table.CheckConstraint("ck_classification_state", "\"State\" IN ('Pending','Leased','RetryScheduled','Completed','Failed')");
                    table.ForeignKey(
                        name: "FK_results_configurations_ConfigurationId",
                        column: x => x.ConfigurationId,
                        principalSchema: "swiftaid_classification",
                        principalTable: "configurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_results_workspace_organizations_WorkspaceId_OrganizationId",
                        columns: x => new { x.WorkspaceId, x.OrganizationId },
                        principalTable: "workspace_organizations",
                        principalColumns: new[] { "workspace_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "associations",
                schema: "swiftaid_classification",
                columns: table => new
                {
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportRevision = table.Column<long>(type: "bigint", nullable: false),
                    ConfigurationId = table.Column<string>(type: "text", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reused = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_associations", x => new { x.CaseId, x.ReportRevision, x.ConfigurationId });
                    table.ForeignKey(
                        name: "FK_associations_case_events_SourceEventId",
                        column: x => x.SourceEventId,
                        principalTable: "case_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_associations_managed_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_associations_results_ResultId",
                        column: x => x.ResultId,
                        principalSchema: "swiftaid_classification",
                        principalTable: "results",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "swiftaid_classification",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigurationId = table.Column<string>(type: "text", nullable: false),
                    NoticeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    ResultId = table.Column<Guid>(type: "uuid", nullable: true),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox", x => new { x.MessageId, x.ConfigurationId });
                    table.ForeignKey(
                        name: "FK_inbox_results_ResultId",
                        column: x => x.ResultId,
                        principalSchema: "swiftaid_classification",
                        principalTable: "results",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "swiftaid_classification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReportRevision = table.Column<long>(type: "bigint", nullable: true),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    ExhaustedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_outbox_results_ResultId",
                        column: x => x.ResultId,
                        principalSchema: "swiftaid_classification",
                        principalTable: "results",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_associations_ResultId",
                schema: "swiftaid_classification",
                table: "associations",
                column: "ResultId");

            migrationBuilder.CreateIndex(
                name: "IX_associations_SourceEventId",
                schema: "swiftaid_classification",
                table: "associations",
                column: "SourceEventId");

            migrationBuilder.CreateIndex(
                name: "IX_inbox_ResultId",
                schema: "swiftaid_classification",
                table: "inbox",
                column: "ResultId");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_AvailableAtUtc_CreatedAtUtc",
                schema: "swiftaid_classification",
                table: "outbox",
                columns: new[] { "AvailableAtUtc", "CreatedAtUtc" },
                filter: "\"PublishedAtUtc\" IS NULL AND \"ExhaustedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_ResultId",
                schema: "swiftaid_classification",
                table: "outbox",
                column: "ResultId");

            migrationBuilder.CreateIndex(
                name: "IX_results_ConfigurationId",
                schema: "swiftaid_classification",
                table: "results",
                column: "ConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_results_Fingerprint",
                schema: "swiftaid_classification",
                table: "results",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_results_State_AvailableAtUtc",
                schema: "swiftaid_classification",
                table: "results",
                columns: new[] { "State", "AvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_results_WorkspaceId_OrganizationId",
                schema: "swiftaid_classification",
                table: "results",
                columns: new[] { "WorkspaceId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "associations",
                schema: "swiftaid_classification");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "swiftaid_classification");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "swiftaid_classification");

            migrationBuilder.DropTable(
                name: "results",
                schema: "swiftaid_classification");

            migrationBuilder.DropTable(
                name: "configurations",
                schema: "swiftaid_classification");
        }
    }
}
