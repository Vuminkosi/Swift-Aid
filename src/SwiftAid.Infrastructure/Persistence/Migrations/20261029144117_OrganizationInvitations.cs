using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "invitation_id",
                table: "membership_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "organization_invitations",
                columns: table => new
                {
                    invitation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    max_uses = table.Column<int>(type: "integer", nullable: false),
                    use_count = table.Column<int>(type: "integer", nullable: false),
                    created_by_actor_id = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_invitations", x => x.invitation_id);
                    table.CheckConstraint("ck_invitations_ids_nonempty", "length(btrim(organization_id)) > 0 AND length(btrim(code_hash)) > 0");
                    table.CheckConstraint("ck_invitations_status", "status IN ('Active', 'Revoked', 'Exhausted', 'Expired')");
                    table.CheckConstraint("ck_invitations_uses_nonnegative", "use_count >= 0 AND max_uses > 0 AND use_count <= max_uses");
                    table.ForeignKey(
                        name: "fk_invitations_organization",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_organization_invitations_organization_id",
                table: "organization_invitations",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_organization_invitations_code_hash",
                table: "organization_invitations",
                column: "code_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_invitations");

            migrationBuilder.DropColumn(
                name: "invitation_id",
                table: "membership_requests");
        }
    }
}
