using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommunitySessionOrganizationMaterialApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "organization_id",
                table: "community_sessions",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                INSERT INTO organization_owners
                    (owner_assignment_id, organization_id, actor_id, assigned_at_utc, assigned_by_actor_id, revoked_at_utc, revoked_by_actor_id)
                SELECT gen_random_uuid(), organization_id, owner_actor_id, created_at_utc, owner_actor_id, NULL, NULL
                FROM organizations legacy_owner
                WHERE owner_actor_id IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM organization_owners assignment
                    WHERE assignment.organization_id = legacy_owner.organization_id
                      AND assignment.actor_id = legacy_owner.owner_actor_id);

                WITH unique_owner_scope AS (
                    SELECT assignment.actor_id, MIN(assignment.organization_id) AS organization_id
                    FROM organization_owners assignment
                    JOIN organizations organization ON organization.organization_id = assignment.organization_id
                    WHERE assignment.revoked_at_utc IS NULL AND organization.status = 'Active'
                    GROUP BY assignment.actor_id
                    HAVING COUNT(DISTINCT assignment.organization_id) = 1)
                UPDATE community_sessions session
                SET organization_id = scope.organization_id
                FROM unique_owner_scope scope
                WHERE session.organization_id IS NULL AND session.created_by = scope.actor_id;
                """);

            migrationBuilder.CreateTable(
                name: "session_material_approvals",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    material_id = table.Column<string>(type: "text", nullable: false),
                    source_version = table.Column<string>(type: "text", nullable: false),
                    rights_evidence = table.Column<string>(type: "text", nullable: false),
                    approved_by_actor_id = table.Column<string>(type: "text", nullable: false),
                    approved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_by_actor_id = table.Column<string>(type: "text", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_material_approvals", x => new { x.organization_id, x.material_id, x.source_version });
                    table.CheckConstraint("ck_session_material_approval_ids", "length(btrim(organization_id)) > 0 AND length(btrim(material_id)) > 0 AND length(btrim(source_version)) > 0 AND length(btrim(rights_evidence)) > 0 AND length(btrim(approved_by_actor_id)) > 0");
                    table.CheckConstraint("ck_session_material_approval_revocation", "(revoked_at_utc IS NULL AND revoked_by_actor_id IS NULL) OR (revoked_at_utc IS NOT NULL AND length(btrim(revoked_by_actor_id)) > 0)");
                    table.ForeignKey(
                        name: "fk_session_material_approvals_approver",
                        column: x => x.approved_by_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_material_approvals_organization",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_material_approvals_revoked_by_actor",
                        column: x => x.revoked_by_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_community_sessions_organization_schedule",
                table: "community_sessions",
                columns: new[] { "organization_id", "start_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_material_approvals_approved_by_actor_id",
                table: "session_material_approvals",
                column: "approved_by_actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_material_approvals_revoked_by_actor_id",
                table: "session_material_approvals",
                column: "revoked_by_actor_id");

            migrationBuilder.AddForeignKey(
                name: "fk_community_sessions_organization",
                table: "community_sessions",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "organization_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_community_sessions_organization",
                table: "community_sessions");

            migrationBuilder.DropTable(
                name: "session_material_approvals");

            migrationBuilder.DropIndex(
                name: "ix_community_sessions_organization_schedule",
                table: "community_sessions");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "community_sessions");
        }
    }
}
