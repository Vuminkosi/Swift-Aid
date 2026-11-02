using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "community_session_subscriptions",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<string>(type: "text", maxLength: 200, nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    consent_reference = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_community_session_subscriptions", x => new { x.session_id, x.account_id });
                    table.CheckConstraint("ck_community_session_subscriptions_channel", "channel IN ('Email', 'MobilePush')");
                    table.CheckConstraint("ck_community_session_subscriptions_status", "status IN ('Active', 'Unsubscribed')");
                    table.CheckConstraint("ck_community_session_subscriptions_values", "length(btrim(account_id)) > 0 AND length(btrim(channel)) > 0 AND length(btrim(consent_reference)) > 0 AND version > 0");
                    table.ForeignKey(
                        name: "fk_community_session_subscriptions_session",
                        column: x => x.session_id,
                        principalTable: "community_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_community_session_subscriptions_account_status",
                table: "community_session_subscriptions",
                columns: new[] { "account_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "community_session_subscriptions");
        }
    }
}
