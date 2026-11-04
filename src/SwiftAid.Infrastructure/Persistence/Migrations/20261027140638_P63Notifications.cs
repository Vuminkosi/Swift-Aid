using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P63Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "swiftaid_notifications");

            migrationBuilder.CreateTable(
                name: "notification_items",
                schema: "swiftaid_notifications",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accepted_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_key = table.Column<string>(type: "text", nullable: false),
                    recipient_actor_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    resource_kind = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    recipient_requirement = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dismissed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_items", x => x.notification_id);
                    table.CheckConstraint("ck_notification_items_accepted_event_nonempty", "accepted_event_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_notification_items_action", "action IN ('OpenCase')");
                    table.CheckConstraint("ck_notification_items_actor_nonempty", "length(btrim(recipient_actor_id)) > 0");
                    table.CheckConstraint("ck_notification_items_dismissed_after_created", "dismissed_at_utc IS NULL OR dismissed_at_utc >= created_at_utc");
                    table.CheckConstraint("ck_notification_items_kind", "kind IN ('CaseNeedsAttention')");
                    table.CheckConstraint("ck_notification_items_occurrence_key_nonempty", "length(btrim(occurrence_key)) > 0");
                    table.CheckConstraint("ck_notification_items_read_after_created", "read_at_utc IS NULL OR read_at_utc >= created_at_utc");
                    table.CheckConstraint("ck_notification_items_recipient_requirement", "recipient_requirement IN ('CurrentAccountableOwner', 'AuthorizedCaseMember')");
                    table.CheckConstraint("ck_notification_items_resource_kind", "resource_kind IN ('ManagedCase')");
                    table.ForeignKey(
                        name: "fk_notification_items_accepted_event",
                        column: x => x.accepted_event_id,
                        principalTable: "case_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notification_items_managed_case",
                        column: x => x.resource_id,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notification_items_recipient_actor",
                        column: x => x.recipient_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "push_devices",
                schema: "swiftaid_notifications",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    sound_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    protected_token = table.Column<byte[]>(type: "bytea", nullable: false),
                    token_fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_push_devices", x => x.device_id);
                    table.CheckConstraint("ck_push_devices_actor_nonempty", "length(btrim(actor_id)) > 0");
                    table.CheckConstraint("ck_push_devices_installation_nonempty", "installation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_push_devices_platform", "platform IN ('Android', 'Ios', 'Web')");
                    table.CheckConstraint("ck_push_devices_token_material", "length(protected_token) > 0 AND octet_length(token_fingerprint) = 32");
                    table.ForeignKey(
                        name: "fk_push_devices_actor",
                        column: x => x.actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_delivery_outbox",
                schema: "swiftaid_notifications",
                columns: table => new
                {
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    available_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    exhausted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_delivery_outbox", x => x.outbox_id);
                    table.CheckConstraint("ck_notification_delivery_outbox_attempt_count", "attempt_count BETWEEN 0 AND 8");
                    table.CheckConstraint("ck_notification_delivery_outbox_lease_pair", "(lease_id IS NULL) = (lease_until_utc IS NULL)");
                    table.CheckConstraint("ck_notification_delivery_outbox_not_both_terminal", "published_at_utc IS NULL OR exhausted_at_utc IS NULL");
                    table.CheckConstraint("ck_notification_delivery_outbox_terminal_state", "(published_at_utc IS NULL OR lease_id IS NULL) AND (exhausted_at_utc IS NULL OR lease_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_notification_delivery_outbox_notification",
                        column: x => x.notification_id,
                        principalSchema: "swiftaid_notifications",
                        principalTable: "notification_items",
                        principalColumn: "notification_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_delivery_outbox_pending",
                schema: "swiftaid_notifications",
                table: "notification_delivery_outbox",
                columns: new[] { "available_at_utc", "created_at_utc", "outbox_id" },
                filter: "published_at_utc IS NULL AND exhausted_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notification_delivery_outbox_notification",
                schema: "swiftaid_notifications",
                table: "notification_delivery_outbox",
                column: "notification_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_items_accepted_event_id",
                schema: "swiftaid_notifications",
                table: "notification_items",
                column: "accepted_event_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_items_recipient_created",
                schema: "swiftaid_notifications",
                table: "notification_items",
                columns: new[] { "recipient_actor_id", "created_at_utc", "notification_id" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_items_resource_id",
                schema: "swiftaid_notifications",
                table: "notification_items",
                column: "resource_id");

            migrationBuilder.CreateIndex(
                name: "ux_notification_items_recipient_occurrence",
                schema: "swiftaid_notifications",
                table: "notification_items",
                columns: new[] { "recipient_actor_id", "occurrence_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_push_devices_actor_installation",
                schema: "swiftaid_notifications",
                table: "push_devices",
                columns: new[] { "actor_id", "installation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_push_devices_token_fingerprint",
                schema: "swiftaid_notifications",
                table: "push_devices",
                column: "token_fingerprint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE
                    "swiftaid_notifications"."notification_delivery_outbox",
                    "swiftaid_notifications"."notification_items",
                    "swiftaid_notifications"."push_devices"
                IN ACCESS EXCLUSIVE MODE;
                """);

            migrationBuilder.Sql(
                """
                DO $swiftaid_p63_notifications_rollback_guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "swiftaid_notifications"."notification_delivery_outbox" LIMIT 1)
                        OR EXISTS (SELECT 1 FROM "swiftaid_notifications"."notification_items" LIMIT 1)
                        OR EXISTS (SELECT 1 FROM "swiftaid_notifications"."push_devices" LIMIT 1)
                    THEN
                        RAISE EXCEPTION USING
                            MESSAGE = 'P63Notifications cannot be rolled back after notification or device data is written; use forward recovery or restore a verified backup.';
                    END IF;
                END
                $swiftaid_p63_notifications_rollback_guard$;
                """);

            migrationBuilder.DropTable(
                name: "notification_delivery_outbox",
                schema: "swiftaid_notifications");

            migrationBuilder.DropTable(
                name: "push_devices",
                schema: "swiftaid_notifications");

            migrationBuilder.DropTable(
                name: "notification_items",
                schema: "swiftaid_notifications");
        }
    }
}
