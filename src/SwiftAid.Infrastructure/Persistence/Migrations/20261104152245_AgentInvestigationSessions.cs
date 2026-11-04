using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentInvestigationSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ellai_sessions_status",
                table: "ellai_sessions");

            migrationBuilder.AddColumn<string>(
                name: "decision_json",
                table: "ellai_utterances",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_investigations",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "text", nullable: false),
                    owner_actor_id = table.Column<string>(type: "text", nullable: false),
                    protected_request = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_investigations", x => x.session_id);
                    table.ForeignKey(
                        name: "FK_agent_investigations_actors_owner_actor_id",
                        column: x => x.owner_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_investigations_ellai_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "ellai_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_ellai_sessions_status",
                table: "ellai_sessions",
                sql: "status IN ('Active', 'Paused', 'Completed', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_agent_investigations_owner_actor_id_created_at_utc",
                table: "agent_investigations",
                columns: new[] { "owner_actor_id", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_investigations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ellai_sessions_status",
                table: "ellai_sessions");

            migrationBuilder.DropColumn(
                name: "decision_json",
                table: "ellai_utterances");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ellai_sessions_status",
                table: "ellai_sessions",
                sql: "status IN ('Active', 'Completed', 'Cancelled')");
        }
    }
}
