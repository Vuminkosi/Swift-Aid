using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionReminderGlobalDueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_session_reminder_occurrences_global_due",
                table: "session_reminder_occurrences",
                columns: new[] { "status", "remind_at_utc", "occurrence_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_session_reminder_occurrences_global_due",
                table: "session_reminder_occurrences");
        }
    }
}
