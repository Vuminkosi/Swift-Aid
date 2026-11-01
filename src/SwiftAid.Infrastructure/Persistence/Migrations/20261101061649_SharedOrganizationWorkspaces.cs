using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedOrganizationWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_access_grant_groups_workspace_scope",
                table: "access_grant_groups");

            migrationBuilder.DropForeignKey(
                name: "fk_managed_cases_workspace_scope",
                table: "managed_cases");

            migrationBuilder.DropIndex(
                name: "IX_case_imports_WorkspaceId",
                schema: "swiftaid_ingestion",
                table: "case_imports");

            migrationBuilder.AddColumn<string>(
                name: "OrganizationId",
                schema: "swiftaid_ingestion",
                table: "case_imports",
                type: "text",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workspace_organizations",
                columns: table => new
                {
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_organizations", x => new { x.workspace_id, x.organization_id });
                    table.CheckConstraint("ck_workspace_organizations_status", "status IN ('Active', 'Suspended', 'Revoked')");
                    table.ForeignKey(
                        name: "FK_workspace_organizations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workspace_organizations_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "workspace_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                INSERT INTO public.workspace_organizations (workspace_id, organization_id, status, created_at_utc, updated_at_utc)
                SELECT workspace_id, organization_id, status, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM public.workspaces;

                CREATE SCHEMA IF NOT EXISTS swiftaid_internal;
                CREATE FUNCTION swiftaid_internal.lock_workspace_organization(p_workspace_id text, p_organization_id text)
                RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $function$
                BEGIN
                    PERFORM organization_id FROM public.organizations WHERE organization_id = p_organization_id AND status = 'Active' FOR SHARE;
                    IF NOT FOUND THEN RETURN false; END IF;
                    PERFORM workspace_id FROM public.workspaces WHERE workspace_id = p_workspace_id AND status = 'Active' FOR SHARE;
                    IF NOT FOUND THEN RETURN false; END IF;
                    RETURN EXISTS (SELECT 1 FROM public.workspace_organizations
                        WHERE workspace_id = p_workspace_id AND organization_id = p_organization_id AND status = 'Active');
                END;
                $function$;
                REVOKE ALL ON FUNCTION swiftaid_internal.lock_workspace_organization(text,text) FROM PUBLIC;

                -- Existing case authority leases hold this same workspace anchor FOR SHARE.
                -- A link update cannot commit until an authorized operation using its old state finishes.
                CREATE FUNCTION swiftaid_internal.lock_workspace_link_change()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $function$
                BEGIN
                    IF TG_OP = 'UPDATE' AND (NEW.workspace_id <> OLD.workspace_id OR NEW.organization_id <> OLD.organization_id) THEN
                        RAISE EXCEPTION 'Workspace organization link identity is immutable.';
                    END IF;
                    PERFORM workspace_id FROM public.workspaces
                        WHERE workspace_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.workspace_id ELSE NEW.workspace_id END FOR UPDATE;
                    IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
                END;
                $function$;
                REVOKE ALL ON FUNCTION swiftaid_internal.lock_workspace_link_change() FROM PUBLIC;
                CREATE TRIGGER workspace_link_authority_lock BEFORE INSERT OR UPDATE OR DELETE ON public.workspace_organizations
                    FOR EACH ROW EXECUTE FUNCTION swiftaid_internal.lock_workspace_link_change();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_case_imports_WorkspaceId_OrganizationId",
                schema: "swiftaid_ingestion",
                table: "case_imports",
                columns: new[] { "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_organizations_organization_id_workspace_id",
                table: "workspace_organizations",
                columns: new[] { "organization_id", "workspace_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_access_grant_groups_workspace_scope",
                table: "access_grant_groups",
                columns: new[] { "workspace_id", "organization_id" },
                principalTable: "workspace_organizations",
                principalColumns: new[] { "workspace_id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_case_imports_workspace_organizations_WorkspaceId_Organizati~",
                schema: "swiftaid_ingestion",
                table: "case_imports",
                columns: new[] { "WorkspaceId", "OrganizationId" },
                principalTable: "workspace_organizations",
                principalColumns: new[] { "workspace_id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_managed_cases_workspace_scope",
                table: "managed_cases",
                columns: new[] { "workspace_id", "organization_id" },
                principalTable: "workspace_organizations",
                principalColumns: new[] { "workspace_id", "organization_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$ BEGIN
                    IF EXISTS (SELECT 1 FROM public.workspace_organizations link JOIN public.workspaces workspace USING (workspace_id)
                        WHERE link.organization_id <> workspace.organization_id) THEN
                        RAISE EXCEPTION 'Shared workspace links exist. Preserve them and fix forward; automatic downgrade is unsafe.';
                    END IF;
                END $guard$;
                DROP TRIGGER workspace_link_authority_lock ON public.workspace_organizations;
                DROP FUNCTION swiftaid_internal.lock_workspace_link_change();
                DROP FUNCTION swiftaid_internal.lock_workspace_organization(text,text);
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_access_grant_groups_workspace_scope",
                table: "access_grant_groups");

            migrationBuilder.DropForeignKey(
                name: "FK_case_imports_workspace_organizations_WorkspaceId_Organizati~",
                schema: "swiftaid_ingestion",
                table: "case_imports");

            migrationBuilder.DropForeignKey(
                name: "fk_managed_cases_workspace_scope",
                table: "managed_cases");

            migrationBuilder.DropTable(
                name: "workspace_organizations");

            migrationBuilder.DropIndex(
                name: "IX_case_imports_WorkspaceId_OrganizationId",
                schema: "swiftaid_ingestion",
                table: "case_imports");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "swiftaid_ingestion",
                table: "case_imports");

            migrationBuilder.CreateIndex(
                name: "IX_case_imports_WorkspaceId",
                schema: "swiftaid_ingestion",
                table: "case_imports",
                column: "WorkspaceId");

            migrationBuilder.AddForeignKey(
                name: "fk_access_grant_groups_workspace_scope",
                table: "access_grant_groups",
                columns: new[] { "workspace_id", "organization_id" },
                principalTable: "workspaces",
                principalColumns: new[] { "workspace_id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_managed_cases_workspace_scope",
                table: "managed_cases",
                columns: new[] { "workspace_id", "organization_id" },
                principalTable: "workspaces",
                principalColumns: new[] { "workspace_id", "organization_id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
