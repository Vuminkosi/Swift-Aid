using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EllaiUtterances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ellai_utterances",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "text", nullable: false),
                    client_utterance_id = table.Column<string>(type: "text", nullable: false),
                    accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ellai_utterances", x => new { x.session_id, x.client_utterance_id });
                    table.CheckConstraint("ck_ellai_utterances_ids_nonempty", "length(btrim(session_id)) > 0 AND length(btrim(client_utterance_id)) > 0");
                    table.ForeignKey(
                        name: "fk_ellai_utterances_session",
                        column: x => x.session_id,
                        principalTable: "ellai_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ellai_utterances_session",
                table: "ellai_utterances",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ellai_utterances");
        }
    }
}
