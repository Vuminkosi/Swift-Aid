using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P10SessionScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "community_sessions",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    audience_description = table.Column<string>(type: "text", nullable: false),
                    topic_tags = table.Column<string>(type: "jsonb", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    region_id = table.Column<string>(type: "text", nullable: false),
                    sector = table.Column<string>(type: "text", nullable: false),
                    presenter = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    time_zone_id = table.Column<string>(type: "text", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: true),
                    materials = table.Column<string>(type: "jsonb", nullable: false),
                    visibility = table.Column<string>(type: "text", nullable: false),
                    channel_type = table.Column<string>(type: "text", nullable: false),
                    channel_link = table.Column<string>(type: "text", nullable: true),
                    reminder_lead_hours = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_by = table.Column<string>(type: "text", nullable: true),
                    reviewed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    self_review_acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    scheduled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_version = table.Column<long>(type: "bigint", nullable: false),
                    cancellation_reason = table.Column<string>(type: "text", nullable: true),
                    cancelled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_community_sessions", x => x.session_id);
                    table.CheckConstraint("ck_community_sessions_channel", "channel_type IN ('None', 'FacebookPage', 'WhatsAppGroup', 'Other')");
                    table.CheckConstraint("ck_community_sessions_duration", "duration_minutes BETWEEN 1 AND 1440");
                    table.CheckConstraint("ck_community_sessions_ids_nonempty", "length(btrim(title)) > 0 AND length(btrim(country_code)) > 0 AND length(btrim(region_id)) > 0 AND length(btrim(created_by)) > 0 AND length(btrim(time_zone_id)) > 0");
                    table.CheckConstraint("ck_community_sessions_status", "status IN ('Draft', 'Reviewed', 'Scheduled', 'Completed', 'Cancelled')");
                    table.CheckConstraint("ck_community_sessions_version", "version > 0");
                    table.CheckConstraint("ck_community_sessions_visibility", "visibility IN ('Public', 'Internal')");
                });

            migrationBuilder.CreateTable(
                name: "community_session_revisions",
                columns: table => new
                {
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_community_session_revisions", x => x.revision_id);
                    table.CheckConstraint("ck_community_session_revisions_actor", "length(btrim(actor_id)) > 0 AND length(btrim(reason)) > 0");
                    table.CheckConstraint("ck_community_session_revisions_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_community_session_revisions_session",
                        column: x => x.session_id,
                        principalTable: "community_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_reminder_occurrences",
                columns: table => new
                {
                    occurrence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    remind_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lead_hours = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_reminder_occurrences", x => x.occurrence_id);
                    table.CheckConstraint("ck_session_reminder_occurrences_lead", "lead_hours BETWEEN 1 AND 168");
                    table.CheckConstraint("ck_session_reminder_occurrences_status", "status IN ('Pending', 'Suppressed', 'Sent')");
                    table.CheckConstraint("ck_session_reminder_occurrences_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_session_reminder_occurrences_session",
                        column: x => x.session_id,
                        principalTable: "community_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_community_session_revisions_session_version",
                table: "community_session_revisions",
                columns: new[] { "session_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_community_sessions_public_listing",
                table: "community_sessions",
                columns: new[] { "visibility", "status", "start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_session_reminder_occurrences_due",
                table: "session_reminder_occurrences",
                columns: new[] { "session_id", "status", "remind_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_session_reminder_occurrences_session_version_lead",
                table: "session_reminder_occurrences",
                columns: new[] { "session_id", "version", "lead_hours" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "community_session_revisions");

            migrationBuilder.DropTable(
                name: "session_reminder_occurrences");

            migrationBuilder.DropTable(
                name: "community_sessions");
        }
    }
}
