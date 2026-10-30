using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EllaiTopics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ellai_topics",
                columns: table => new
                {
                    topic_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    aliases_json = table.Column<string>(type: "text", nullable: false),
                    entity_ref = table.Column<string>(type: "text", nullable: true),
                    entity_version = table.Column<string>(type: "text", nullable: true),
                    coverage_state = table.Column<string>(type: "text", nullable: false),
                    checkpoint_id = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ellai_topics", x => x.topic_id);
                    table.CheckConstraint("ck_ellai_topics_ids_nonempty", "length(btrim(topic_id)) > 0 AND length(btrim(session_id)) > 0 AND length(btrim(title)) > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_ellai_topics_session",
                table: "ellai_topics",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ellai_topics");
        }
    }
}
