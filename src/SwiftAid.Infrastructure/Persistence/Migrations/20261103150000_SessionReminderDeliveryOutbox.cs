using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionReminderDeliveryOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_reminder_deliveries",
                columns: table => new
                {
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    session_version = table.Column<long>(type: "bigint", nullable: false),
                    subscription_version = table.Column<long>(type: "bigint", nullable: false),
                    consent_reference = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    available_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_reminder_deliveries", x => x.delivery_id);
                    table.CheckConstraint("ck_session_reminder_deliveries_channel", "channel IN ('Email', 'MobilePush')");
                    table.CheckConstraint("ck_session_reminder_deliveries_lease", "(lease_id IS NULL) = (lease_until_utc IS NULL)");
                    table.CheckConstraint("ck_session_reminder_deliveries_status", "status IN ('Pending', 'Submitted', 'Suppressed', 'OutcomeUnknown')");
                    table.CheckConstraint("ck_session_reminder_deliveries_versions", "session_version > 0 AND subscription_version > 0 AND attempt_count >= 0");
                    table.ForeignKey(
                        name: "fk_session_reminder_deliveries_session",
                        column: x => x.session_id,
                        principalTable: "community_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_session_reminder_deliveries_claim",
                table: "session_reminder_deliveries",
                columns: new[] { "status", "available_at_utc", "lease_until_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_session_reminder_deliveries_session_status",
                table: "session_reminder_deliveries",
                columns: new[] { "session_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_session_reminder_deliveries_occurrence_recipient_version_channel",
                table: "session_reminder_deliveries",
                columns: new[] { "occurrence_id", "account_id", "subscription_version", "channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_reminder_deliveries");
        }
    }
}
