using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P21BLockTokenAndCaseOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "swiftaid_internal");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations");

            migrationBuilder.AddColumn<short>(
                name: "lock_token",
                table: "workspaces",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "lock_token",
                table: "organizations",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "lock_token",
                table: "organization_memberships",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "lock_token",
                table: "managed_cases",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "lock_token",
                table: "managed_case_regions",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "lock_token",
                table: "case_shares",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "lock_token",
                table: "actors",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "lock_token",
                table: "actor_access_states",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "lock_token",
                table: "access_grant_groups",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "lock_token",
                table: "access_grant_group_regions",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "lock_token",
                table: "access_grant_group_operations",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_workspaces_lock_token_zero",
                table: "workspaces",
                sql: "lock_token = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organizations_lock_token_zero",
                table: "organizations",
                sql: "lock_token = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organization_memberships_lock_token_zero",
                table: "organization_memberships",
                sql: "lock_token = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_managed_cases_lock_token_zero",
                table: "managed_cases",
                sql: "lock_token = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_managed_case_regions_lock_token_zero",
                table: "managed_case_regions",
                sql: "lock_token = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_shares_lock_token_zero",
                table: "case_shares",
                sql: "lock_token = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_actors_lock_token_zero",
                table: "actors",
                sql: "lock_token = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_actor_access_states_lock_token_zero",
                table: "actor_access_states",
                sql: "lock_token = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_grant_groups_lock_token_zero",
                table: "access_grant_groups",
                sql: "lock_token = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_grant_group_regions_lock_token_zero",
                table: "access_grant_group_regions",
                sql: "lock_token = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_grant_group_operations_lock_token_zero",
                table: "access_grant_group_operations",
                sql: "lock_token = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations",
                sql: "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $p21b_down_guard$
                DECLARE
                    helper_schema_oid oid;
                BEGIN
                    IF EXISTS (
                        SELECT 1
                          FROM public.access_grant_group_operations
                         WHERE operation IN (
                             'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase'
                         )
                    ) THEN
                        RAISE EXCEPTION 'Cannot downgrade P21B while a grant uses a newly added case operation.';
                    END IF;

                    IF pg_catalog.to_regprocedure(
                        'swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)'
                    ) IS NOT NULL THEN
                        RAISE EXCEPTION 'Cannot downgrade P21B while the case authority lock helper is installed.';
                    END IF;

                    SELECT namespace.oid
                      INTO helper_schema_oid
                      FROM pg_catalog.pg_namespace AS namespace
                     WHERE namespace.nspname = 'swiftaid_internal';

                    IF helper_schema_oid IS NOT NULL THEN
                        IF EXISTS (
                            SELECT 1
                              FROM pg_catalog.pg_depend AS dependency
                             WHERE dependency.refclassid = 'pg_catalog.pg_namespace'::pg_catalog.regclass
                               AND dependency.refobjid = helper_schema_oid
                               AND dependency.deptype <> 'p'
                        ) THEN
                            RAISE EXCEPTION 'Cannot downgrade P21B while swiftaid_internal contains database objects.';
                        END IF;

                        IF EXISTS (
                            SELECT 1
                              FROM pg_catalog.pg_namespace AS namespace
                              CROSS JOIN LATERAL pg_catalog.aclexplode(namespace.nspacl) AS privilege
                             WHERE namespace.oid = helper_schema_oid
                               AND privilege.grantee <> namespace.nspowner
                        ) THEN
                            RAISE EXCEPTION 'Cannot downgrade P21B while swiftaid_internal has non-owner grants.';
                        END IF;

                        IF NOT EXISTS (
                            SELECT 1
                              FROM pg_catalog.pg_roles AS role
                             WHERE role.oid = (
                                 SELECT namespace.nspowner
                                   FROM pg_catalog.pg_namespace AS namespace
                                  WHERE namespace.oid = helper_schema_oid
                             )
                               AND role.rolname = current_user
                        ) THEN
                            RAISE EXCEPTION 'Cannot downgrade P21B because the migration role does not own swiftaid_internal.';
                        END IF;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                          FROM pg_catalog.pg_attribute AS attribute
                          JOIN pg_catalog.pg_class AS relation ON relation.oid = attribute.attrelid
                          JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                          CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) AS privilege
                         WHERE namespace.nspname = 'public'
                           AND relation.relname = ANY (ARRAY[
                               'organizations', 'workspaces', 'actor_access_states', 'actors',
                               'organization_memberships', 'access_grant_groups',
                               'access_grant_group_regions', 'access_grant_group_operations',
                               'case_shares', 'managed_cases', 'managed_case_regions'
                           ])
                           AND attribute.attname = 'lock_token'
                           AND NOT attribute.attisdropped
                           AND privilege.privilege_type = 'UPDATE'
                           AND privilege.grantee <> relation.relowner
                    ) THEN
                        RAISE EXCEPTION 'Cannot downgrade P21B while lock_token has non-owner UPDATE grants.';
                    END IF;
                END
                $p21b_down_guard$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_workspaces_lock_token_zero",
                table: "workspaces");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organizations_lock_token_zero",
                table: "organizations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organization_memberships_lock_token_zero",
                table: "organization_memberships");

            migrationBuilder.DropCheckConstraint(
                name: "ck_managed_cases_lock_token_zero",
                table: "managed_cases");

            migrationBuilder.DropCheckConstraint(
                name: "ck_managed_case_regions_lock_token_zero",
                table: "managed_case_regions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_case_shares_lock_token_zero",
                table: "case_shares");

            migrationBuilder.DropCheckConstraint(
                name: "ck_actors_lock_token_zero",
                table: "actors");

            migrationBuilder.DropCheckConstraint(
                name: "ck_actor_access_states_lock_token_zero",
                table: "actor_access_states");

            migrationBuilder.DropCheckConstraint(
                name: "ck_access_grant_groups_lock_token_zero",
                table: "access_grant_groups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_access_grant_group_regions_lock_token_zero",
                table: "access_grant_group_regions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_access_grant_group_operations_lock_token_zero",
                table: "access_grant_group_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "organization_memberships");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "managed_cases");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "managed_case_regions");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "case_shares");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "actors");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "actor_access_states");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "access_grant_groups");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "access_grant_group_regions");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "access_grant_group_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations",
                sql: "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence')");
        }
    }
}
