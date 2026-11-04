using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SurveillanceIntelligenceLoop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "swiftaid_surveillance");

            migrationBuilder.CreateTable(
                name: "signals",
                schema: "swiftaid_surveillance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WorkspaceId = table.Column<string>(type: "text", nullable: false),
                    OrganizationId = table.Column<string>(type: "text", nullable: false),
                    Area = table.Column<string>(type: "text", nullable: false),
                    Syndrome = table.Column<string>(type: "text", nullable: false),
                    WindowEndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Candidate = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewState = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_signals_workspace_organizations_WorkspaceId_OrganizationId",
                        columns: x => new { x.WorkspaceId, x.OrganizationId },
                        principalTable: "workspace_organizations",
                        principalColumns: new[] { "workspace_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "work",
                schema: "swiftaid_surveillance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work", x => x.Id);
                    table.CheckConstraint("ck_surveillance_work_attempts", "\"Attempts\" BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_surveillance_work_state", "\"State\" IN ('Pending','RetryScheduled','Completed','Failed')");
                    table.ForeignKey(
                        name: "FK_work_managed_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "swiftaid_surveillance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FailureCode = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox1", x => x.Id);
                    table.ForeignKey(
                        name: "FK_outbox_signals_SignalId",
                        column: x => x.SignalId,
                        principalSchema: "swiftaid_surveillance",
                        principalTable: "signals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assessments",
                schema: "swiftaid_surveillance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    WorkId = table.Column<string>(type: "character varying(64)", nullable: false),
                    EvidenceHash = table.Column<string>(type: "text", nullable: false),
                    AssessmentJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assessments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assessments_signals_SignalId",
                        column: x => x.SignalId,
                        principalSchema: "swiftaid_surveillance",
                        principalTable: "signals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assessments_work_WorkId",
                        column: x => x.WorkId,
                        principalSchema: "swiftaid_surveillance",
                        principalTable: "work",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inputs",
                schema: "swiftaid_surveillance",
                columns: table => new
                {
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<string>(type: "text", nullable: false),
                    OrganizationId = table.Column<string>(type: "text", nullable: false),
                    ObservationJson = table.Column<string>(type: "jsonb", nullable: false),
                    WorkId = table.Column<string>(type: "character varying(64)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inputs", x => x.CaseId);
                    table.ForeignKey(
                        name: "FK_inputs_managed_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inputs_work_WorkId",
                        column: x => x.WorkId,
                        principalSchema: "swiftaid_surveillance",
                        principalTable: "work",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "evidence",
                schema: "swiftaid_surveillance",
                columns: table => new
                {
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportRevision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence", x => new { x.AssessmentId, x.CaseId });
                    table.ForeignKey(
                        name: "FK_evidence_assessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalSchema: "swiftaid_surveillance",
                        principalTable: "assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evidence_case_events_SourceEventId",
                        column: x => x.SourceEventId,
                        principalTable: "case_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evidence_managed_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reviews",
                schema: "swiftaid_surveillance",
                columns: table => new
                {
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviews", x => x.CommandId);
                    table.ForeignKey(
                        name: "FK_reviews_actors_ActorId",
                        column: x => x.ActorId,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reviews_assessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalSchema: "swiftaid_surveillance",
                        principalTable: "assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reviews_signals_SignalId",
                        column: x => x.SignalId,
                        principalSchema: "swiftaid_surveillance",
                        principalTable: "signals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assessments_SignalId_Version",
                schema: "swiftaid_surveillance",
                table: "assessments",
                columns: new[] { "SignalId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assessments_WorkId",
                schema: "swiftaid_surveillance",
                table: "assessments",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_CaseId",
                schema: "swiftaid_surveillance",
                table: "evidence",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_SourceEventId",
                schema: "swiftaid_surveillance",
                table: "evidence",
                column: "SourceEventId");

            migrationBuilder.CreateIndex(
                name: "IX_inputs_WorkId",
                schema: "swiftaid_surveillance",
                table: "inputs",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_inputs_WorkspaceId_OrganizationId",
                schema: "swiftaid_surveillance",
                table: "inputs",
                columns: new[] { "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_AvailableAtUtc",
                schema: "swiftaid_surveillance",
                table: "outbox",
                column: "AvailableAtUtc",
                filter: "\"PublishedAtUtc\" IS NULL AND \"Attempts\" < 5");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_SignalId_Version",
                schema: "swiftaid_surveillance",
                table: "outbox",
                columns: new[] { "SignalId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reviews_ActorId",
                schema: "swiftaid_surveillance",
                table: "reviews",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_reviews_AssessmentId",
                schema: "swiftaid_surveillance",
                table: "reviews",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_reviews_SignalId_Version",
                schema: "swiftaid_surveillance",
                table: "reviews",
                columns: new[] { "SignalId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signals_Key",
                schema: "swiftaid_surveillance",
                table: "signals",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signals_WorkspaceId_OrganizationId",
                schema: "swiftaid_surveillance",
                table: "signals",
                columns: new[] { "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_work_CaseId",
                schema: "swiftaid_surveillance",
                table: "work",
                column: "CaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evidence",
                schema: "swiftaid_surveillance");

            migrationBuilder.DropTable(
                name: "inputs",
                schema: "swiftaid_surveillance");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "swiftaid_surveillance");

            migrationBuilder.DropTable(
                name: "reviews",
                schema: "swiftaid_surveillance");

            migrationBuilder.DropTable(
                name: "assessments",
                schema: "swiftaid_surveillance");

            migrationBuilder.DropTable(
                name: "signals",
                schema: "swiftaid_surveillance");

            migrationBuilder.DropTable(
                name: "work",
                schema: "swiftaid_surveillance");
        }
    }
}
