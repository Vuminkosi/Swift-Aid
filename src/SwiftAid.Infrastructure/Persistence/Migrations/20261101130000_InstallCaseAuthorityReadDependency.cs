using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

/// <summary>Install the canonical lock algorithm without elevating the caller's database permissions.</summary>
[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261101130000_InstallCaseAuthorityReadDependency")]
public sealed class InstallCaseAuthorityReadDependency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Preserve an operator-provisioned restricted SECURITY DEFINER helper and its ACLs.
        // The invoker fallback is suitable for the existing owner-backed Compose deployment.
        migrationBuilder.Sql("""
DO $install$
BEGIN
    IF to_regprocedure('swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)') IS NULL THEN
        EXECUTE $definition$
CREATE OR REPLACE FUNCTION swiftaid_internal.lock_case_authority(
    p_case_id uuid,
    p_actor_id text,
    p_actor_hints text[],
    p_write boolean
)
RETURNS smallint
LANGUAGE plpgsql
VOLATILE
PARALLEL UNSAFE
SECURITY INVOKER
SET search_path = pg_catalog
AS $lock_helper$
DECLARE
    v_initial_organization_id text;
    v_initial_workspace_id text;
    v_initial_owner_actor_id text;
    v_current_organization_id text;
    v_current_workspace_id text;
    v_current_owner_actor_id text;
    v_actor_id text;
    v_grant_group_id text;
    v_grant_group_ids text[] := ARRAY[]::text[];
BEGIN
    -- Result codes: 0=locked; 1=caller must roll back and rediscover; 2=required
    -- case/authority row unavailable; 3=invalid argument. None is an access decision.
    IF p_case_id IS NULL
       OR p_actor_id IS NULL
       OR pg_catalog.btrim(p_actor_id) = ''
       OR p_actor_hints IS NULL
       OR pg_catalog.cardinality(p_actor_hints) = 0
       OR p_write IS NULL THEN
        RETURN 3;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.unnest(p_actor_hints) AS hint(actor_id)
        WHERE hint.actor_id IS NULL
           OR pg_catalog.btrim(hint.actor_id) = ''
           OR hint.actor_id <> pg_catalog.btrim(hint.actor_id)
    ) THEN
        RETURN 3;
    END IF;

    IF pg_catalog.cardinality(p_actor_hints) <>
       (SELECT pg_catalog.count(DISTINCT hint.actor_id)::integer
        FROM pg_catalog.unnest(p_actor_hints) AS hint(actor_id))
       OR NOT (p_actor_id = ANY (p_actor_hints)) THEN
        RETURN 3;
    END IF;

    -- This read is intentionally unlocked. The caller supplies its canonical,
    -- sorted actor hints; if discovery changed, the final locked read returns 1.
    SELECT managed_case.organization_id,
           managed_case.workspace_id,
           managed_case.accountable_owner_actor_id
      INTO v_initial_organization_id,
           v_initial_workspace_id,
           v_initial_owner_actor_id
      FROM public.managed_cases AS managed_case
     WHERE managed_case.case_id = p_case_id;

    IF NOT FOUND THEN
        RETURN 2;
    END IF;

    IF NOT (v_initial_owner_actor_id = ANY (p_actor_hints)) THEN
        RETURN 1;
    END IF;

    PERFORM organization.organization_id
      FROM public.organizations AS organization
     WHERE organization.organization_id = v_initial_organization_id
     ORDER BY organization.organization_id
       FOR SHARE;
    IF NOT FOUND THEN
        RETURN 2;
    END IF;

    PERFORM workspace.workspace_id
      FROM public.workspaces AS workspace
     WHERE workspace.workspace_id = v_initial_workspace_id
     ORDER BY workspace.workspace_id
       FOR SHARE;
    IF NOT FOUND THEN
        RETURN 2;
    END IF;

    -- Each authority set is locked in its canonical table/key order. The input
    -- hint order does not control physical lock acquisition.
    FOR v_actor_id IN
        SELECT hint.actor_id
          FROM pg_catalog.unnest(p_actor_hints) AS hint(actor_id)
         ORDER BY hint.actor_id
    LOOP
        IF p_write THEN
            PERFORM access_state.actor_id
              FROM public.actor_access_states AS access_state
             WHERE access_state.actor_id = v_actor_id
               FOR UPDATE;
        ELSE
            PERFORM access_state.actor_id
              FROM public.actor_access_states AS access_state
             WHERE access_state.actor_id = v_actor_id
               FOR SHARE;
        END IF;
        IF NOT FOUND THEN
            RETURN 2;
        END IF;
    END LOOP;

    FOR v_actor_id IN
        SELECT hint.actor_id
          FROM pg_catalog.unnest(p_actor_hints) AS hint(actor_id)
         ORDER BY hint.actor_id
    LOOP
        PERFORM actor.actor_id
          FROM public.actors AS actor
         WHERE actor.actor_id = v_actor_id
           FOR SHARE;
        IF NOT FOUND THEN
            RETURN 2;
        END IF;
    END LOOP;

    PERFORM membership.membership_id
      FROM public.organization_memberships AS membership
     WHERE membership.organization_id = v_initial_organization_id
       AND membership.actor_id = ANY (p_actor_hints)
     ORDER BY membership.actor_id, membership.membership_id
       FOR SHARE;

    FOR v_grant_group_id IN
        SELECT grant_group.grant_group_id
          FROM public.access_grant_groups AS grant_group
         WHERE grant_group.organization_id = v_initial_organization_id
           AND grant_group.actor_id = ANY (p_actor_hints)
         ORDER BY grant_group.grant_group_id
           FOR SHARE
    LOOP
        v_grant_group_ids := pg_catalog.array_append(v_grant_group_ids, v_grant_group_id);
    END LOOP;

    PERFORM grant_region.grant_group_id
      FROM public.access_grant_group_regions AS grant_region
     WHERE grant_region.grant_group_id = ANY (v_grant_group_ids)
     ORDER BY grant_region.grant_group_id, grant_region.region_id
       FOR SHARE;

    PERFORM grant_operation.grant_group_id
      FROM public.access_grant_group_operations AS grant_operation
     WHERE grant_operation.grant_group_id = ANY (v_grant_group_ids)
     ORDER BY grant_operation.grant_group_id, grant_operation.operation
       FOR SHARE;

    PERFORM case_share.actor_id
      FROM public.case_shares AS case_share
     WHERE case_share.case_id = p_case_id
       AND case_share.actor_id = ANY (p_actor_hints)
     ORDER BY case_share.actor_id
       FOR SHARE;

    IF p_write THEN
        SELECT managed_case.organization_id,
               managed_case.workspace_id,
               managed_case.accountable_owner_actor_id
          INTO v_current_organization_id,
               v_current_workspace_id,
               v_current_owner_actor_id
          FROM public.managed_cases AS managed_case
         WHERE managed_case.case_id = p_case_id
           FOR UPDATE;
    ELSE
        SELECT managed_case.organization_id,
               managed_case.workspace_id,
               managed_case.accountable_owner_actor_id
          INTO v_current_organization_id,
               v_current_workspace_id,
               v_current_owner_actor_id
          FROM public.managed_cases AS managed_case
         WHERE managed_case.case_id = p_case_id
           FOR SHARE;
    END IF;

    IF NOT FOUND THEN
        RETURN 2;
    END IF;

    IF v_initial_organization_id IS DISTINCT FROM v_current_organization_id
       OR v_initial_workspace_id IS DISTINCT FROM v_current_workspace_id
       OR NOT (v_current_owner_actor_id = ANY (p_actor_hints)) THEN
        RETURN 1;
    END IF;

    PERFORM case_region.region_id
      FROM public.managed_case_regions AS case_region
     WHERE case_region.case_id = p_case_id
     ORDER BY case_region.region_id
       FOR SHARE;

    RETURN 0;
END;
$lock_helper$;
$definition$;
        REVOKE ALL ON FUNCTION swiftaid_internal.lock_case_authority(uuid,text,text[],boolean) FROM PUBLIC;
    END IF;
END
$install$;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("The authority helper is required by running readers. Fix forward without removing it.");
}
