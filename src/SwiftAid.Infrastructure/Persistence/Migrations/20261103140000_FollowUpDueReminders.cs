using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FollowUpDueReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_items_recipient_requirement",
                schema: "swiftaid_notifications",
                table: "notification_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_managed_case_follow_ups_versions",
                table: "managed_case_follow_ups");

            migrationBuilder.AddColumn<Guid>(
                name: "related_resource_id",
                schema: "swiftaid_notifications",
                table: "notification_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "related_resource_version",
                schema: "swiftaid_notifications",
                table: "notification_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "current_schedule_event_id",
                table: "managed_case_follow_ups",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "current_schedule_version",
                table: "managed_case_follow_ups",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql("""
                UPDATE managed_case_follow_ups AS follow_up
                SET current_schedule_version = CASE
                        WHEN follow_up.status = 'Scheduled' AND follow_up.outcome_version IS NOT NULL
                            THEN follow_up.outcome_version
                        ELSE follow_up.scheduled_version
                    END,
                    current_schedule_event_id = schedule_event.event_id
                FROM case_events AS schedule_event
                WHERE schedule_event.case_id = follow_up.case_id
                  AND schedule_event.aggregate_version = CASE
                        WHEN follow_up.status = 'Scheduled' AND follow_up.outcome_version IS NOT NULL
                            THEN follow_up.outcome_version
                        ELSE follow_up.scheduled_version
                    END;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "current_schedule_event_id",
                table: "managed_case_follow_ups",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
            migrationBuilder.AlterColumn<long>(
                name: "current_schedule_version",
                table: "managed_case_follow_ups",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_items_recipient_requirement",
                schema: "swiftaid_notifications",
                table: "notification_items",
                sql: "recipient_requirement IN ('CurrentAccountableOwner', 'AuthorizedCaseMember', 'CurrentFollowUpAssignee')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_items_related_resource_pair",
                schema: "swiftaid_notifications",
                table: "notification_items",
                sql: "(related_resource_id IS NULL) = (related_resource_version IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_items_follow_up_reference",
                schema: "swiftaid_notifications",
                table: "notification_items",
                sql: "(recipient_requirement = 'CurrentFollowUpAssignee') = (related_resource_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_managed_case_follow_ups_current_schedule_event_id",
                table: "managed_case_follow_ups",
                column: "current_schedule_event_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_managed_case_follow_ups_versions",
                table: "managed_case_follow_ups",
                sql: "scheduled_version > 0 AND current_schedule_version > 0 AND (outcome_version IS NULL OR outcome_version > 0)");

            migrationBuilder.AddForeignKey(
                name: "fk_managed_case_follow_ups_current_schedule_event",
                table: "managed_case_follow_ups",
                column: "current_schedule_event_id",
                principalTable: "case_events",
                principalColumn: "event_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_managed_case_follow_ups_current_schedule_event",
                table: "managed_case_follow_ups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_items_recipient_requirement",
                schema: "swiftaid_notifications",
                table: "notification_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_items_related_resource_pair",
                schema: "swiftaid_notifications",
                table: "notification_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_items_follow_up_reference",
                schema: "swiftaid_notifications",
                table: "notification_items");

            migrationBuilder.DropIndex(
                name: "IX_managed_case_follow_ups_current_schedule_event_id",
                table: "managed_case_follow_ups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_managed_case_follow_ups_versions",
                table: "managed_case_follow_ups");

            migrationBuilder.DropColumn(
                name: "related_resource_id",
                schema: "swiftaid_notifications",
                table: "notification_items");

            migrationBuilder.DropColumn(
                name: "related_resource_version",
                schema: "swiftaid_notifications",
                table: "notification_items");

            migrationBuilder.DropColumn(
                name: "current_schedule_event_id",
                table: "managed_case_follow_ups");

            migrationBuilder.DropColumn(
                name: "current_schedule_version",
                table: "managed_case_follow_ups");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_items_recipient_requirement",
                schema: "swiftaid_notifications",
                table: "notification_items",
                sql: "recipient_requirement IN ('CurrentAccountableOwner', 'AuthorizedCaseMember')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_managed_case_follow_ups_versions",
                table: "managed_case_follow_ups",
                sql: "scheduled_version > 0 AND (outcome_version IS NULL OR outcome_version > 0)");
        }
    }
}
