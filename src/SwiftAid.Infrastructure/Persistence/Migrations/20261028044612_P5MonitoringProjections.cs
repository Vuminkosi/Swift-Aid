using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P5MonitoringProjections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations");

            migrationBuilder.CreateTable(
                name: "managed_case_escalations",
                columns: table => new
                {
                    escalation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    raised_version = table.Column<long>(type: "bigint", nullable: false),
                    acknowledged_version = table.Column<long>(type: "bigint", nullable: true),
                    closed_version = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_managed_case_escalations", x => x.escalation_id);
                    table.CheckConstraint("ck_managed_case_escalations_status", "status IN ('Raised', 'Acknowledged', 'Closed')");
                    table.CheckConstraint("ck_managed_case_escalations_versions", "raised_version > 0 AND (acknowledged_version IS NULL OR acknowledged_version > 0) AND (closed_version IS NULL OR closed_version > 0)");
                    table.ForeignKey(
                        name: "fk_managed_case_escalations_case",
                        column: x => x.case_id,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_case_follow_ups",
                columns: table => new
                {
                    follow_up_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    assignee_actor_id = table.Column<string>(type: "text", nullable: false),
                    due_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    scheduled_version = table.Column<long>(type: "bigint", nullable: false),
                    outcome_version = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_managed_case_follow_ups", x => x.follow_up_id);
                    table.CheckConstraint("ck_managed_case_follow_ups_status", "status IN ('Scheduled', 'Completed', 'Missed', 'Unreachable', 'Cancelled')");
                    table.CheckConstraint("ck_managed_case_follow_ups_text", "length(btrim(purpose)) > 0 AND length(btrim(assignee_actor_id)) > 0");
                    table.CheckConstraint("ck_managed_case_follow_ups_versions", "scheduled_version > 0 AND (outcome_version IS NULL OR outcome_version > 0)");
                    table.ForeignKey(
                        name: "fk_managed_case_follow_ups_case",
                        column: x => x.case_id,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_case_observations",
                columns: table => new
                {
                    observation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    narrative = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    aggregate_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_managed_case_observations", x => x.observation_id);
                    table.CheckConstraint("ck_managed_case_observations_text", "length(btrim(narrative)) > 0 AND length(btrim(source)) > 0");
                    table.CheckConstraint("ck_managed_case_observations_version", "aggregate_version > 0");
                    table.ForeignKey(
                        name: "fk_managed_case_observations_case",
                        column: x => x.case_id,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events",
                sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations",
                sql: "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase', 'ReviewIntake', 'CoordinateIntake', 'RecordObservation', 'ManageFollowUp', 'ManageEscalation')");

            migrationBuilder.CreateIndex(
                name: "ix_managed_case_escalations_case",
                table: "managed_case_escalations",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_managed_case_follow_ups_case",
                table: "managed_case_follow_ups",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_managed_case_observations_case",
                table: "managed_case_observations",
                column: "case_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "managed_case_escalations");

            migrationBuilder.DropTable(
                name: "managed_case_follow_ups");

            migrationBuilder.DropTable(
                name: "managed_case_observations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events",
                sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations",
                sql: "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase', 'ReviewIntake', 'CoordinateIntake')");
        }
    }
}
