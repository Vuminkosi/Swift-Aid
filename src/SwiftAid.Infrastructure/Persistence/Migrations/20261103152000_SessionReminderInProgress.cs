using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionReminderInProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_session_reminder_deliveries_status",
                table: "session_reminder_deliveries");

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_reminder_deliveries_status",
                table: "session_reminder_deliveries",
                sql: "status IN ('Pending', 'InProgress', 'Submitted', 'Suppressed', 'OutcomeUnknown')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_session_reminder_deliveries_status",
                table: "session_reminder_deliveries");

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_reminder_deliveries_status",
                table: "session_reminder_deliveries",
                sql: "status IN ('Pending', 'Submitted', 'Suppressed', 'OutcomeUnknown')");
        }
    }
}
