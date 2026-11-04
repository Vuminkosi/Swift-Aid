using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationOwnerAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_owners",
                columns: table => new
                {
                    owner_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    assigned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_by_actor_id = table.Column<string>(type: "text", nullable: false),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_actor_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_owners", x => x.owner_assignment_id);
                    table.CheckConstraint("ck_organization_owners_ids_nonempty", "length(btrim(organization_id)) > 0 AND length(btrim(actor_id)) > 0 AND length(btrim(assigned_by_actor_id)) > 0");
                    table.CheckConstraint("ck_organization_owners_revocation", "(revoked_at_utc IS NULL AND revoked_by_actor_id IS NULL) OR (revoked_at_utc IS NOT NULL AND length(btrim(revoked_by_actor_id)) > 0)");
                    table.ForeignKey(
                        name: "fk_organization_owners_actor",
                        column: x => x.actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_organization_owners_assigned_by_actor",
                        column: x => x.assigned_by_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_organization_owners_organization",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_organization_owners_revoked_by_actor",
                        column: x => x.revoked_by_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_organization_owners_actor_id",
                table: "organization_owners",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_owners_assigned_by_actor_id",
                table: "organization_owners",
                column: "assigned_by_actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_owners_revoked_by_actor_id",
                table: "organization_owners",
                column: "revoked_by_actor_id");

            migrationBuilder.CreateIndex(
                name: "ux_organization_owners_org_actor",
                table: "organization_owners",
                columns: new[] { "organization_id", "actor_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_owners");
        }
    }
}
