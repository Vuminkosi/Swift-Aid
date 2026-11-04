-- Provision the narrowly scoped row-lock helper after the reviewed schema migration.
--
-- Required psql variables (identifiers are quoted by psql with :"name"):
--   helper_owner  Existing NOLOGIN, nonprivileged function-owner role.
--   runtime_role  Existing restricted application runtime role.
--
-- Preconditions:
--   * The reviewed migration already created swiftaid_internal and DB-enforced constant-zero
--     lock_token columns on all eleven public relations below. Each must have a
--     VALIDATED local CHECK named ck_<table>_lock_token_zero whose canonical
--     pg_get_expr form is exactly (lock_token = 0). This script does not alter the schema.
--   * Roles already exist. This script never creates roles or credentials.
--   * Run as the authorized database/schema operator, never as runtime_role.
--   * The runtime role's case-command write grants are a separate responsibility.

\set ON_ERROR_STOP on

\if :{?helper_owner}
\else
    \echo 'Required psql variable helper_owner is missing.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid lock-helper preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

\if :{?runtime_role}
\else
    \echo 'Required psql variable runtime_role is missing.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid lock-helper preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

SELECT EXISTS (
    SELECT 1
    FROM pg_catalog.pg_roles AS helper
    WHERE helper.rolname = :'helper_owner'
      AND NOT helper.rolcanlogin
      AND NOT helper.rolsuper
      AND NOT helper.rolcreatedb
      AND NOT helper.rolcreaterole
      AND NOT helper.rolreplication
      AND NOT helper.rolinherit
      AND NOT helper.rolbypassrls
      AND NOT EXISTS (
          SELECT 1
          FROM pg_catalog.pg_database AS database
          WHERE database.datname = pg_catalog.current_database()
            AND database.datdba = helper.oid
      )
      AND NOT EXISTS (
          SELECT 1
          FROM pg_catalog.pg_auth_members AS membership
          WHERE membership.member = helper.oid OR membership.roleid = helper.oid
      )
      AND NOT EXISTS (
          SELECT 1
          FROM pg_catalog.pg_namespace AS namespace
          WHERE namespace.nspname IN ('public', 'swiftaid_internal')
            AND namespace.nspowner = helper.oid
      )
      AND NOT EXISTS (
          SELECT 1
          FROM pg_catalog.pg_class AS relation
          JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
          WHERE namespace.nspname = 'public'
            AND relation.relname = ANY (ARRAY[
                'organizations', 'workspaces', 'actor_access_states', 'actors',
                'organization_memberships', 'access_grant_groups',
                'access_grant_group_regions', 'access_grant_group_operations',
                'case_shares', 'managed_cases', 'managed_case_regions'
            ])
            AND relation.relowner = helper.oid
      )
) AS helper_role_precondition \gset

\if :helper_role_precondition
\else
    \echo 'helper_owner is missing or violates the dedicated NOLOGIN role preconditions.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid lock-helper preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

SELECT EXISTS (
    SELECT 1
    FROM pg_catalog.pg_roles AS runtime
    JOIN pg_catalog.pg_roles AS helper ON helper.rolname = :'helper_owner'
    WHERE runtime.rolname = :'runtime_role'
      AND runtime.oid <> helper.oid
      AND NOT runtime.rolsuper
      AND NOT runtime.rolcreatedb
      AND NOT runtime.rolcreaterole
      AND NOT runtime.rolreplication
      AND NOT runtime.rolbypassrls
      AND NOT EXISTS (
          SELECT 1
          FROM pg_catalog.pg_auth_members AS membership
          WHERE membership.member = runtime.oid OR membership.roleid = runtime.oid
      )
      AND NOT EXISTS (
          SELECT 1
          FROM pg_catalog.pg_database AS database
          WHERE database.datname = pg_catalog.current_database()
            AND database.datdba = runtime.oid
      )
      AND NOT EXISTS (
          SELECT 1
          FROM pg_catalog.pg_namespace AS namespace
          WHERE namespace.nspname IN ('public', 'swiftaid_internal')
            AND namespace.nspowner = runtime.oid
      )
      AND NOT EXISTS (
          SELECT 1
          FROM pg_catalog.pg_class AS relation
          JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
          WHERE namespace.nspname = 'public'
            AND relation.relname = ANY (ARRAY[
                'organizations', 'workspaces', 'actor_access_states', 'actors',
                'organization_memberships', 'access_grant_groups',
                'access_grant_group_regions', 'access_grant_group_operations',
                'case_shares', 'managed_cases', 'managed_case_regions'
            ])
            AND relation.relowner = runtime.oid
      )
) AS runtime_role_precondition \gset

\if :runtime_role_precondition
\else
    \echo 'runtime_role is missing or has an unsafe owner, membership, or privilege configuration.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid lock-helper preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

WITH required_relations(relation_name) AS (
    VALUES
        ('organizations'),
        ('workspaces'),
        ('actor_access_states'),
        ('actors'),
        ('organization_memberships'),
        ('access_grant_groups'),
        ('access_grant_group_regions'),
        ('access_grant_group_operations'),
        ('case_shares'),
        ('managed_cases'),
        ('managed_case_regions')
)
SELECT count(*) = 11 AS lock_token_precondition
FROM required_relations AS required
JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = 'public'
JOIN pg_catalog.pg_class AS relation
  ON relation.relnamespace = namespace.oid
 AND relation.relname = required.relation_name
JOIN pg_catalog.pg_attribute AS attribute
  ON attribute.attrelid = relation.oid
 AND attribute.attname = 'lock_token'
 AND attribute.atttypid = 'pg_catalog.int2'::pg_catalog.regtype
 AND attribute.attnotnull
 AND NOT attribute.attisdropped
WHERE relation.relkind IN ('r', 'p') \gset

\if :lock_token_precondition
\else
    \echo 'The reviewed migration must supply a NOT NULL smallint lock_token on all eleven target tables.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid lock-helper preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

-- Require the reviewed migration's exact, validated constant-zero constraint
-- identity and expression on every target. A broader expression or a row-value
-- scan alone is not an acceptable substitute.
WITH expected_constraints(relation_name, constraint_name) AS (
    VALUES
        ('organizations', 'ck_organizations_lock_token_zero'),
        ('workspaces', 'ck_workspaces_lock_token_zero'),
        ('actor_access_states', 'ck_actor_access_states_lock_token_zero'),
        ('actors', 'ck_actors_lock_token_zero'),
        ('organization_memberships', 'ck_organization_memberships_lock_token_zero'),
        ('access_grant_groups', 'ck_access_grant_groups_lock_token_zero'),
        ('access_grant_group_regions', 'ck_access_grant_group_regions_lock_token_zero'),
        ('access_grant_group_operations', 'ck_access_grant_group_operations_lock_token_zero'),
        ('case_shares', 'ck_case_shares_lock_token_zero'),
        ('managed_cases', 'ck_managed_cases_lock_token_zero'),
        ('managed_case_regions', 'ck_managed_case_regions_lock_token_zero')
)
SELECT count(*) = 11 AS lock_token_constraint_precondition
  FROM expected_constraints AS expected
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = 'public'
  JOIN pg_catalog.pg_class AS relation
    ON relation.relnamespace = namespace.oid
   AND relation.relname = expected.relation_name
   AND relation.relkind IN ('r', 'p')
  JOIN pg_catalog.pg_constraint AS constraint_definition
    ON constraint_definition.conrelid = relation.oid
   AND constraint_definition.conname = expected.constraint_name
   AND constraint_definition.contype = 'c'
   AND constraint_definition.convalidated
   AND constraint_definition.conislocal
   AND pg_catalog.pg_get_expr(
       constraint_definition.conbin, constraint_definition.conrelid, false
   ) = '(lock_token = 0)' \gset

\if :lock_token_constraint_precondition
\else
    \echo 'The reviewed migration must add the named VALIDATED CHECK (lock_token = 0) to every target table before helper provisioning.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid lock-helper preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

-- Reject inherited, PUBLIC, or pre-existing helper-owner privileges before
-- provisioning. In particular, an additive column grant cannot neutralize an
-- existing table-wide UPDATE or a column UPDATE on business data.
WITH relation_contract(relation_name, readable_columns) AS (
    VALUES
        ('organizations', ARRAY['organization_id']::text[]),
        ('workspaces', ARRAY['workspace_id']::text[]),
        ('actor_access_states', ARRAY['actor_id']::text[]),
        ('actors', ARRAY['actor_id']::text[]),
        ('organization_memberships', ARRAY['organization_id', 'actor_id', 'membership_id']::text[]),
        ('access_grant_groups', ARRAY['organization_id', 'actor_id', 'grant_group_id']::text[]),
        ('access_grant_group_regions', ARRAY['grant_group_id', 'region_id']::text[]),
        ('access_grant_group_operations', ARRAY['grant_group_id', 'operation']::text[]),
        ('case_shares', ARRAY['case_id', 'actor_id']::text[]),
        ('managed_cases', ARRAY['case_id', 'organization_id', 'workspace_id', 'accountable_owner_actor_id']::text[]),
        ('managed_case_regions', ARRAY['case_id', 'region_id']::text[])
), effective_relations AS (
    SELECT contract.relation_name,
           contract.readable_columns,
           'public.' || contract.relation_name AS qualified_name,
           relation.oid AS relation_id
      FROM relation_contract AS contract
      JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = 'public'
      JOIN pg_catalog.pg_class AS relation
        ON relation.relnamespace = namespace.oid
       AND relation.relname = contract.relation_name
)
SELECT NOT EXISTS (
    SELECT 1
      FROM effective_relations AS target
     WHERE pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'SELECT')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'INSERT')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'UPDATE')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'DELETE')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'TRUNCATE')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'REFERENCES')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'TRIGGER')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'MAINTAIN')
        OR EXISTS (
            SELECT 1
              FROM pg_catalog.pg_attribute AS attribute
             WHERE attribute.attrelid = target.relation_id
               AND attribute.attnum > 0
               AND NOT attribute.attisdropped
               AND (
                   (
                       attribute.attname <> ALL (target.readable_columns)
                       AND pg_catalog.has_column_privilege(
                           :'helper_owner', target.qualified_name, attribute.attname, 'SELECT'
                       )
                   )
                   OR (
                       attribute.attname <> 'lock_token'
                       AND pg_catalog.has_column_privilege(
                           :'helper_owner', target.qualified_name, attribute.attname, 'UPDATE'
                       )
                   )
                   OR pg_catalog.has_column_privilege(
                       :'helper_owner', target.qualified_name, attribute.attname, 'INSERT'
                   )
                   OR pg_catalog.has_column_privilege(
                       :'helper_owner', target.qualified_name, attribute.attname, 'REFERENCES'
                   )
               )
        )
) AS helper_privilege_precondition \gset

\if :helper_privilege_precondition
\else
    \echo 'helper_owner has effective privileges beyond the lock-helper profile; remove PUBLIC, direct, or inherited table/column grants before provisioning.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid lock-helper preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

BEGIN;

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
SECURITY DEFINER
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

-- Transfer the SECURITY DEFINER boundary to a dedicated role. CREATE is
-- temporary; the helper owner must not retain schema creation rights.
GRANT CREATE ON SCHEMA swiftaid_internal TO :"helper_owner";
ALTER FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean)
    OWNER TO :"helper_owner";
REVOKE CREATE ON SCHEMA swiftaid_internal FROM :"helper_owner";

ALTER FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean)
    SET search_path = pg_catalog;

REVOKE ALL ON SCHEMA swiftaid_internal FROM PUBLIC;
REVOKE ALL ON FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean)
    FROM PUBLIC;
REVOKE CREATE ON SCHEMA swiftaid_internal FROM :"runtime_role";

GRANT USAGE ON SCHEMA public TO :"helper_owner";
GRANT USAGE ON SCHEMA swiftaid_internal TO :"helper_owner";
GRANT USAGE ON SCHEMA public TO :"runtime_role";
GRANT USAGE ON SCHEMA swiftaid_internal TO :"runtime_role";

-- The definer can read only the columns used for locking and may UPDATE only
-- the inert privilege-anchor column. The function never writes lock_token.
GRANT SELECT (organization_id) ON public.organizations TO :"helper_owner";
GRANT UPDATE (lock_token) ON public.organizations TO :"helper_owner";
GRANT SELECT (workspace_id) ON public.workspaces TO :"helper_owner";
GRANT UPDATE (lock_token) ON public.workspaces TO :"helper_owner";
GRANT SELECT (actor_id) ON public.actor_access_states TO :"helper_owner";
GRANT UPDATE (lock_token) ON public.actor_access_states TO :"helper_owner";
GRANT SELECT (actor_id) ON public.actors TO :"helper_owner";
GRANT UPDATE (lock_token) ON public.actors TO :"helper_owner";
GRANT SELECT (organization_id, actor_id, membership_id) ON public.organization_memberships TO :"helper_owner";
GRANT UPDATE (lock_token) ON public.organization_memberships TO :"helper_owner";
GRANT SELECT (organization_id, actor_id, grant_group_id) ON public.access_grant_groups TO :"helper_owner";
GRANT UPDATE (lock_token) ON public.access_grant_groups TO :"helper_owner";
GRANT SELECT (grant_group_id, region_id) ON public.access_grant_group_regions TO :"helper_owner";
GRANT UPDATE (lock_token) ON public.access_grant_group_regions TO :"helper_owner";
GRANT SELECT (grant_group_id, operation) ON public.access_grant_group_operations TO :"helper_owner";
GRANT UPDATE (lock_token) ON public.access_grant_group_operations TO :"helper_owner";
GRANT SELECT (case_id, actor_id) ON public.case_shares TO :"helper_owner";
GRANT UPDATE (lock_token) ON public.case_shares TO :"helper_owner";
GRANT SELECT (case_id, organization_id, workspace_id, accountable_owner_actor_id)
    ON public.managed_cases TO :"helper_owner";
GRANT UPDATE (lock_token) ON public.managed_cases TO :"helper_owner";
GRANT SELECT (case_id, region_id) ON public.managed_case_regions TO :"helper_owner";
GRANT UPDATE (lock_token) ON public.managed_case_regions TO :"helper_owner";

-- The current authorization/descriptor stores use EF entity reads. These are
-- read grants only. No runtime UPDATE is granted by this helper artifact.
GRANT SELECT ON public.organizations TO :"runtime_role";
GRANT SELECT ON public.workspaces TO :"runtime_role";
GRANT SELECT ON public.actor_access_states TO :"runtime_role";
GRANT SELECT ON public.actors TO :"runtime_role";
GRANT SELECT ON public.organization_memberships TO :"runtime_role";
GRANT SELECT ON public.access_grant_groups TO :"runtime_role";
GRANT SELECT ON public.access_grant_group_regions TO :"runtime_role";
GRANT SELECT ON public.access_grant_group_operations TO :"runtime_role";
GRANT SELECT ON public.case_shares TO :"runtime_role";
GRANT SELECT ON public.managed_cases TO :"runtime_role";
GRANT SELECT ON public.managed_case_regions TO :"runtime_role";

GRANT EXECUTE ON FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean)
    TO :"runtime_role";

-- Fail closed if effective runtime privileges can create in the helper schema
-- or write the helper's privilege-anchor columns.
SELECT NOT pg_catalog.has_schema_privilege(:'runtime_role', 'swiftaid_internal', 'CREATE')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.organizations', 'UPDATE')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.workspaces', 'UPDATE')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.actor_access_states', 'UPDATE')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.actors', 'UPDATE')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.organization_memberships', 'UPDATE')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.access_grant_groups', 'UPDATE')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.access_grant_group_regions', 'UPDATE')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.access_grant_group_operations', 'UPDATE')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.case_shares', 'UPDATE')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.managed_cases', 'UPDATE')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.managed_case_regions', 'UPDATE')
   AND NOT EXISTS (
       SELECT 1
         FROM pg_catalog.pg_class AS relation
         JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
         JOIN pg_catalog.pg_attribute AS attribute ON attribute.attrelid = relation.oid
        WHERE namespace.nspname = 'public'
          AND relation.relname = ANY (ARRAY[
              'organizations', 'workspaces', 'actor_access_states', 'actors',
              'organization_memberships', 'access_grant_groups',
              'access_grant_group_regions', 'access_grant_group_operations',
              'case_shares', 'managed_cases', 'managed_case_regions'
          ])
          AND attribute.attnum > 0
          AND NOT attribute.attisdropped
          AND pg_catalog.has_column_privilege(
              :'runtime_role', relation.oid, attribute.attname, 'UPDATE'
          )
   )
   AND pg_catalog.has_function_privilege(
       :'runtime_role',
       'swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)',
       'EXECUTE'
   ) AS runtime_acl_valid \gset

\if :runtime_acl_valid
\else
    \echo 'Runtime lock-helper ACL validation failed; transaction will be rolled back.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid lock-helper postcondition failed.' USING ERRCODE = 'P0001'; END $$;
\endif

-- Confirm the function owner has exactly the required effective table/column
-- profile after provisioning. This also detects PUBLIC ACLs that an additive
-- grant would otherwise leave in force.
WITH relation_contract(relation_name, readable_columns) AS (
    VALUES
        ('organizations', ARRAY['organization_id']::text[]),
        ('workspaces', ARRAY['workspace_id']::text[]),
        ('actor_access_states', ARRAY['actor_id']::text[]),
        ('actors', ARRAY['actor_id']::text[]),
        ('organization_memberships', ARRAY['organization_id', 'actor_id', 'membership_id']::text[]),
        ('access_grant_groups', ARRAY['organization_id', 'actor_id', 'grant_group_id']::text[]),
        ('access_grant_group_regions', ARRAY['grant_group_id', 'region_id']::text[]),
        ('access_grant_group_operations', ARRAY['grant_group_id', 'operation']::text[]),
        ('case_shares', ARRAY['case_id', 'actor_id']::text[]),
        ('managed_cases', ARRAY['case_id', 'organization_id', 'workspace_id', 'accountable_owner_actor_id']::text[]),
        ('managed_case_regions', ARRAY['case_id', 'region_id']::text[])
), effective_relations AS (
    SELECT contract.relation_name,
           contract.readable_columns,
           'public.' || contract.relation_name AS qualified_name,
           relation.oid AS relation_id
      FROM relation_contract AS contract
      JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = 'public'
      JOIN pg_catalog.pg_class AS relation
        ON relation.relnamespace = namespace.oid
       AND relation.relname = contract.relation_name
)
SELECT NOT EXISTS (
    SELECT 1
      FROM effective_relations AS target
     WHERE pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'SELECT')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'INSERT')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'UPDATE')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'DELETE')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'TRUNCATE')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'REFERENCES')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'TRIGGER')
        OR pg_catalog.has_table_privilege(:'helper_owner', target.qualified_name, 'MAINTAIN')
        OR EXISTS (
            SELECT 1
              FROM pg_catalog.pg_attribute AS attribute
             WHERE attribute.attrelid = target.relation_id
               AND attribute.attnum > 0
               AND NOT attribute.attisdropped
               AND (
                   pg_catalog.has_column_privilege(
                       :'helper_owner', target.qualified_name, attribute.attname, 'SELECT'
                   ) IS DISTINCT FROM (attribute.attname = ANY (target.readable_columns))
                   OR pg_catalog.has_column_privilege(
                       :'helper_owner', target.qualified_name, attribute.attname, 'UPDATE'
                   ) IS DISTINCT FROM (attribute.attname = 'lock_token')
                   OR pg_catalog.has_column_privilege(
                       :'helper_owner', target.qualified_name, attribute.attname, 'INSERT'
                   )
                   OR pg_catalog.has_column_privilege(
                       :'helper_owner', target.qualified_name, attribute.attname, 'REFERENCES'
                   )
               )
        )
) AS helper_privilege_profile_valid \gset

\if :helper_privilege_profile_valid
\else
    \echo 'helper_owner effective privileges do not exactly match the lock-helper profile; transaction will be rolled back.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid lock-helper postcondition failed.' USING ERRCODE = 'P0001'; END $$;
\endif

COMMIT;
