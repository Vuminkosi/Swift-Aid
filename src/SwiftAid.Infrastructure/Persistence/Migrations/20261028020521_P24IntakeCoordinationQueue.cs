using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P24IntakeCoordinationQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations");

            migrationBuilder.AddColumn<string>(
                name: "clarification_reason",
                schema: "swiftaid_intake",
                table: "intake_reports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "clarification_requested_at_utc",
                schema: "swiftaid_intake",
                table: "intake_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "coordinator_actor_id",
                schema: "swiftaid_intake",
                table: "intake_reports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "coordinator_assigned_at_utc",
                schema: "swiftaid_intake",
                table: "intake_reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "routing_area_id",
                schema: "swiftaid_intake",
                table: "intake_reports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "routing_catalogue_version",
                schema: "swiftaid_intake",
                table: "intake_reports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "routing_country_code",
                schema: "swiftaid_intake",
                table: "intake_reports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "routing_region_id",
                schema: "swiftaid_intake",
                table: "intake_reports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject_scope",
                schema: "swiftaid_intake",
                table: "intake_reports",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "intake_coordination_actions",
                schema: "swiftaid_intake",
                columns: table => new
                {
                    action_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    coordination_organization_id = table.Column<string>(type: "text", nullable: false),
                    grant_group_id = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    from_status = table.Column<string>(type: "text", nullable: false),
                    from_routing = table.Column<string>(type: "text", nullable: false),
                    from_queue_id = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: false),
                    to_routing = table.Column<string>(type: "text", nullable: false),
                    to_queue_id = table.Column<string>(type: "text", nullable: true),
                    resulting_version = table.Column<long>(type: "bigint", nullable: false),
                    acted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_coordination_actions", x => x.action_id);
                    table.CheckConstraint("ck_intake_coordination_action", "action IN ('ClaimForReview', 'AssignQueue', 'RequestClarification', 'RecordNoServiceCoverage')");
                    table.CheckConstraint("ck_intake_coordination_actor", "length(btrim(actor_id)) > 0 AND length(btrim(coordination_organization_id)) > 0 AND length(btrim(grant_group_id)) > 0");
                    table.CheckConstraint("ck_intake_coordination_version", "resulting_version > 0");
                    table.ForeignKey(
                        name: "fk_intake_coordination_actions_report",
                        column: x => x.receipt_id,
                        principalSchema: "swiftaid_intake",
                        principalTable: "intake_reports",
                        principalColumn: "receipt_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_intake_reports_coordination_queue",
                schema: "swiftaid_intake",
                table: "intake_reports",
                columns: new[] { "routing_status", "received_at_utc", "receipt_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations",
                sql: "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase', 'ReviewIntake', 'CoordinateIntake')");

            migrationBuilder.CreateIndex(
                name: "ix_intake_coordination_actions_receipt",
                schema: "swiftaid_intake",
                table: "intake_coordination_actions",
                columns: new[] { "receipt_id", "acted_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "intake_coordination_actions",
                schema: "swiftaid_intake");

            migrationBuilder.DropIndex(
                name: "ix_intake_reports_coordination_queue",
                schema: "swiftaid_intake",
                table: "intake_reports");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations");

            migrationBuilder.DropColumn(
                name: "clarification_reason",
                schema: "swiftaid_intake",
                table: "intake_reports");

            migrationBuilder.DropColumn(
                name: "clarification_requested_at_utc",
                schema: "swiftaid_intake",
                table: "intake_reports");

            migrationBuilder.DropColumn(
                name: "coordinator_actor_id",
                schema: "swiftaid_intake",
                table: "intake_reports");

            migrationBuilder.DropColumn(
                name: "coordinator_assigned_at_utc",
                schema: "swiftaid_intake",
                table: "intake_reports");

            migrationBuilder.DropColumn(
                name: "routing_area_id",
                schema: "swiftaid_intake",
                table: "intake_reports");

            migrationBuilder.DropColumn(
                name: "routing_catalogue_version",
                schema: "swiftaid_intake",
                table: "intake_reports");

            migrationBuilder.DropColumn(
                name: "routing_country_code",
                schema: "swiftaid_intake",
                table: "intake_reports");

            migrationBuilder.DropColumn(
                name: "routing_region_id",
                schema: "swiftaid_intake",
                table: "intake_reports");

            migrationBuilder.DropColumn(
                name: "subject_scope",
                schema: "swiftaid_intake",
                table: "intake_reports");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations",
                sql: "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase', 'ReviewIntake')");
        }
    }
}
