using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EllaiSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ellai_sessions",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    cycles_used = table.Column<int>(type: "integer", nullable: false),
                    cycles_allowed = table.Column<int>(type: "integer", nullable: false),
                    control_fence = table.Column<long>(type: "bigint", nullable: false),
                    interaction_epoch = table.Column<long>(type: "bigint", nullable: false),
                    speech_epoch = table.Column<long>(type: "bigint", nullable: false),
                    scope_epoch = table.Column<string>(type: "text", nullable: false),
                    active_investigation_id = table.Column<string>(type: "text", nullable: true),
                    pending_checkpoint_id = table.Column<string>(type: "text", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ellai_sessions", x => x.session_id);
                    table.CheckConstraint("ck_ellai_sessions_cycles", "cycles_used >= 0 AND cycles_allowed BETWEEN 1 AND 12");
                    table.CheckConstraint("ck_ellai_sessions_ids_nonempty", "length(btrim(session_id)) > 0 AND length(btrim(scope_epoch)) > 0");
                    table.CheckConstraint("ck_ellai_sessions_status", "status IN ('Active', 'Completed', 'Cancelled')");
                    table.CheckConstraint("ck_ellai_sessions_version", "version > 0");
                });

            migrationBuilder.CreateTable(
                name: "ellai_checkpoints",
                columns: table => new
                {
                    checkpoint_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    session_version = table.Column<long>(type: "bigint", nullable: false),
                    turn_id = table.Column<string>(type: "text", nullable: true),
                    topic_refs = table.Column<string>(type: "jsonb", nullable: false),
                    pending_receipt_ids = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ellai_checkpoints", x => x.checkpoint_id);
                    table.CheckConstraint("ck_ellai_checkpoints_ids_nonempty", "length(btrim(checkpoint_id)) > 0 AND length(btrim(session_id)) > 0");
                    table.ForeignKey(
                        name: "fk_ellai_checkpoints_session",
                        column: x => x.session_id,
                        principalTable: "ellai_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ellai_checkpoints_session",
                table: "ellai_checkpoints",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_ellai_sessions_status",
                table: "ellai_sessions",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ellai_checkpoints");

            migrationBuilder.DropTable(
                name: "ellai_sessions");
        }
    }
}
