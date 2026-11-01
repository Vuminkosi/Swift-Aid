-- Grant the reviewed case-command writer profile to an existing restricted
-- LOGIN role, after case-authority-lock-helper.sql has been provisioned.
-- This is additive and idempotent only for the exact allowlisted columns.
-- It does not create roles, grant role membership, change helper ownership, or
-- grant outbox publisher permissions.
--
-- Required psql variable (identifier is quoted with :"runtime_role"):
--   runtime_role  Existing restricted application runtime role.
--
-- Run as the authorized database operator. Do not use the application role.

\set ON_ERROR_STOP on

\if :{?runtime_role}
\else
    \echo 'Required psql variable runtime_role is missing.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid runtime-grants preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

SELECT EXISTS (
    SELECT 1
      FROM pg_catalog.pg_roles AS runtime
     WHERE runtime.rolname = :'runtime_role'
       AND runtime.rolcanlogin
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
                  'case_shares', 'managed_cases', 'managed_case_regions',
                  'protected_event_payloads', 'case_events', 'command_receipts',
                  'domain_outbox', 'security_audit_entries'
              ])
              AND relation.relowner = runtime.oid
       )
) AS runtime_role_precondition \gset

\if :runtime_role_precondition
\else
    \echo 'runtime_role is missing or has an unsafe login, owner, membership, or privilege configuration.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid runtime-grants preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

WITH required_relations(relation_name) AS (
    VALUES
        ('organizations'),
        ('workspaces'),
        ('workspace_organizations'),
        ('actor_access_states'),
        ('actors'),
        ('organization_memberships'),
        ('access_grant_groups'),
        ('access_grant_group_regions'),
        ('access_grant_group_operations'),
        ('case_shares'),
        ('managed_cases'),
        ('managed_case_regions'),
        ('protected_event_payloads'),
        ('case_events'),
        ('command_receipts'),
        ('domain_outbox'),
        ('security_audit_entries')
)
SELECT count(*) = 17 AS required_relations_precondition
  FROM required_relations AS required
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = 'public'
  JOIN pg_catalog.pg_class AS relation
    ON relation.relnamespace = namespace.oid
   AND relation.relname = required.relation_name
   AND relation.relkind IN ('r', 'p') \gset

\if :required_relations_precondition
\else
    \echo 'The reviewed persistence migrations must create all case-command runtime grant targets.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid runtime-grants preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

-- Do not layer the allowlist over PUBLIC write grants. This checks table and
-- column ACLs directly, including privileges that the runtime could inherit.
WITH write_privileges(privilege_type) AS (
    VALUES ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE'), ('REFERENCES'), ('TRIGGER'), ('MAINTAIN')
)
SELECT NOT EXISTS (
    SELECT 1
      FROM pg_catalog.pg_class AS relation
      JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
      CROSS JOIN LATERAL pg_catalog.aclexplode(relation.relacl) AS privilege
      JOIN write_privileges AS allowed ON allowed.privilege_type = privilege.privilege_type
     WHERE namespace.nspname = 'public'
       AND relation.relname = ANY (ARRAY[
           'organizations', 'workspaces', 'actor_access_states', 'actors',
           'organization_memberships', 'access_grant_groups',
           'access_grant_group_regions', 'access_grant_group_operations',
           'case_shares', 'managed_cases', 'managed_case_regions',
           'protected_event_payloads', 'case_events', 'command_receipts',
           'domain_outbox', 'security_audit_entries'
       ])
       AND privilege.grantee = 0
) AND NOT EXISTS (
    SELECT 1
      FROM pg_catalog.pg_class AS relation
      JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
      JOIN pg_catalog.pg_attribute AS attribute ON attribute.attrelid = relation.oid
      CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) AS privilege
      JOIN write_privileges AS allowed ON allowed.privilege_type = privilege.privilege_type
     WHERE namespace.nspname = 'public'
       AND relation.relname = ANY (ARRAY[
           'organizations', 'workspaces', 'actor_access_states', 'actors',
           'organization_memberships', 'access_grant_groups',
           'access_grant_group_regions', 'access_grant_group_operations',
           'case_shares', 'managed_cases', 'managed_case_regions',
           'protected_event_payloads', 'case_events', 'command_receipts',
           'domain_outbox', 'security_audit_entries'
       ])
       AND attribute.attnum > 0
       AND NOT attribute.attisdropped
       AND privilege.grantee = 0
) AS no_public_write_precondition \gset

\if :no_public_write_precondition
\else
    \echo 'PUBLIC has write privileges on a case-command persistence table; remove that unsafe grant before provisioning.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid runtime-grants preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

-- Permit reapplication only when any existing effective runtime write already
-- matches this allowlist. Reject table-wide and unrelated column privileges,
-- inherited grants, and unrelated INSERT/DELETE/TRUNCATE/DDL capability.
WITH grant_contract(relation_name, update_columns, insert_columns) AS (
    VALUES
        ('organizations', ARRAY[]::text[], ARRAY[]::text[]),
        ('workspaces', ARRAY[]::text[], ARRAY[]::text[]),
        ('workspace_organizations', ARRAY[]::text[], ARRAY[]::text[]),
        ('actor_access_states', ARRAY['access_epoch', 'version', 'updated_at_utc']::text[], ARRAY[]::text[]),
        ('actors', ARRAY[]::text[], ARRAY[]::text[]),
        ('organization_memberships', ARRAY[]::text[], ARRAY[]::text[]),
        ('access_grant_groups', ARRAY[]::text[], ARRAY[]::text[]),
        ('access_grant_group_regions', ARRAY[]::text[], ARRAY[]::text[]),
        ('access_grant_group_operations', ARRAY[]::text[], ARRAY[]::text[]),
        ('case_shares', ARRAY[]::text[], ARRAY[]::text[]),
        ('managed_cases', ARRAY['aggregate_version', 'report_revision', 'resource_version', 'workflow', 'workflow_version', 'accountable_owner_actor_id', 'updated_at_utc']::text[], ARRAY[]::text[]),
        ('managed_case_regions', ARRAY[]::text[], ARRAY[]::text[]),
        ('protected_event_payloads', ARRAY[]::text[], ARRAY['payload_id', 'protected_bytes', 'protection_purpose_version', 'created_at_utc']::text[]),
        ('case_events', ARRAY[]::text[], ARRAY['event_id', 'case_id', 'aggregate_version', 'report_revision', 'command_id', 'actor_id', 'event_type', 'schema_version', 'occurred_at_utc', 'recorded_at_utc', 'payload_id']::text[]),
        ('command_receipts', ARRAY[]::text[], ARRAY['receipt_id', 'actor_id', 'command_id', 'case_id', 'command_type', 'schema_version', 'canonical_payload_hash', 'first_aggregate_version', 'last_aggregate_version', 'report_revision', 'accepted_at_utc']::text[]),
        ('domain_outbox', ARRAY[]::text[], ARRAY['outbox_id', 'event_id', 'message_type', 'created_at_utc', 'available_at_utc', 'attempt_count', 'lease_until_utc', 'published_at_utc']::text[]),
        ('security_audit_entries', ARRAY[]::text[], ARRAY['audit_id', 'actor_id', 'target_actor_id', 'resource_id', 'operation', 'decision_code', 'access_epoch', 'recorded_at_utc', 'reason_code']::text[])
), effective_relations AS (
    SELECT contract.relation_name,
           contract.update_columns,
           contract.insert_columns,
           'public.' || contract.relation_name AS qualified_name,
           relation.oid AS relation_id
      FROM grant_contract AS contract
      JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = 'public'
      JOIN pg_catalog.pg_class AS relation
        ON relation.relnamespace = namespace.oid
       AND relation.relname = contract.relation_name
)
SELECT NOT EXISTS (
    SELECT 1
      FROM effective_relations AS target
     WHERE pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'INSERT')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'UPDATE')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'DELETE')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'TRUNCATE')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'REFERENCES')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'TRIGGER')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'MAINTAIN')
        OR EXISTS (
            SELECT 1
              FROM pg_catalog.pg_attribute AS attribute
             WHERE attribute.attrelid = target.relation_id
               AND attribute.attnum > 0
               AND NOT attribute.attisdropped
               AND (
                   (pg_catalog.has_column_privilege(:'runtime_role', target.relation_id, attribute.attname, 'UPDATE')
                    AND NOT attribute.attname = ANY (target.update_columns))
                   OR (pg_catalog.has_column_privilege(:'runtime_role', target.relation_id, attribute.attname, 'INSERT')
                    AND NOT attribute.attname = ANY (target.insert_columns))
                   OR pg_catalog.has_column_privilege(:'runtime_role', target.relation_id, attribute.attname, 'REFERENCES')
               )
        )
) AND NOT pg_catalog.has_schema_privilege(:'runtime_role', 'public', 'CREATE')
   AND NOT pg_catalog.has_schema_privilege(:'runtime_role', 'swiftaid_internal', 'CREATE')
   AND CURRENT_USER <> :'runtime_role' AS existing_runtime_acl_precondition \gset

\if :existing_runtime_acl_precondition
\else
    \echo 'runtime_role has privileges outside the approved case-command writer allowlist; remove PUBLIC, inherited, or direct excess grants first.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid runtime-grants preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif

-- Allow only the current SQLAUTH helper and command-journal read profile.
-- public-schema non-extension relations are treated as application relations;
-- pg_catalog, information_schema, and extension-owned relations are outside
-- this allowlist. This includes EF migration history when it exists.
WITH select_allowlist(relation_name) AS (
    VALUES
        ('organizations'),
        ('workspaces'),
        ('workspace_organizations'),
        ('actor_access_states'),
        ('actors'),
        ('organization_memberships'),
        ('access_grant_groups'),
        ('access_grant_group_regions'),
        ('access_grant_group_operations'),
        ('case_shares'),
        ('managed_cases'),
        ('managed_case_regions'),
        ('protected_event_payloads'),
        ('case_events'),
        ('command_receipts')
), application_relations AS (
    SELECT relation.oid, relation.relname
      FROM pg_catalog.pg_class AS relation
      JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
     WHERE namespace.nspname = 'public'
       AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
       AND NOT EXISTS (
           SELECT 1
             FROM pg_catalog.pg_depend AS extension_membership
            WHERE extension_membership.classid = 'pg_catalog.pg_class'::regclass
              AND extension_membership.objid = relation.oid
              AND extension_membership.refclassid = 'pg_catalog.pg_extension'::regclass
              AND extension_membership.deptype = 'e'
       )
)
SELECT NOT EXISTS (
    SELECT 1
      FROM application_relations AS target
     WHERE NOT EXISTS (
         SELECT 1 FROM select_allowlist AS allowed WHERE allowed.relation_name = target.relname
     )
       AND (
           pg_catalog.has_table_privilege(:'runtime_role', target.oid, 'SELECT')
           OR EXISTS (
               SELECT 1
                 FROM pg_catalog.pg_attribute AS attribute
                WHERE attribute.attrelid = target.oid
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped
                  AND pg_catalog.has_column_privilege(:'runtime_role', target.oid, attribute.attname, 'SELECT')
           )
       )
) AND NOT EXISTS (
    SELECT 1
      FROM pg_catalog.pg_class AS sequence
      JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = sequence.relnamespace
     WHERE namespace.nspname = 'public'
       AND sequence.relkind = 'S'
       AND NOT EXISTS (
           SELECT 1
             FROM pg_catalog.pg_depend AS extension_membership
            WHERE extension_membership.classid = 'pg_catalog.pg_class'::regclass
              AND extension_membership.objid = sequence.oid
              AND extension_membership.refclassid = 'pg_catalog.pg_extension'::regclass
              AND extension_membership.deptype = 'e'
       )
       AND (
           pg_catalog.has_sequence_privilege(:'runtime_role', sequence.oid, 'SELECT')
           OR pg_catalog.has_sequence_privilege(:'runtime_role', sequence.oid, 'USAGE')
           OR pg_catalog.has_sequence_privilege(:'runtime_role', sequence.oid, 'UPDATE')
       )
) AS runtime_select_allowlist_precondition \gset

\if :runtime_select_allowlist_precondition
\else
    \echo 'runtime_role has effective SELECT outside the approved SwiftAid relation allowlist; remove PUBLIC, direct, or inherited reads before provisioning.'
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid runtime-grants preflight failed.' USING ERRCODE = 'P0001'; END $$;
\endif
BEGIN;

GRANT UPDATE (access_epoch, version, updated_at_utc)
    ON public.actor_access_states TO :"runtime_role";
GRANT UPDATE (
    aggregate_version,
    report_revision,
    resource_version,
    workflow,
    workflow_version,
    accountable_owner_actor_id,
    updated_at_utc
) ON public.managed_cases TO :"runtime_role";

GRANT SELECT ON public.protected_event_payloads TO :"runtime_role";
GRANT SELECT ON public.case_events TO :"runtime_role";
GRANT SELECT ON public.command_receipts TO :"runtime_role";
GRANT SELECT ON public.workspace_organizations TO :"runtime_role";

GRANT INSERT (payload_id, protected_bytes, protection_purpose_version, created_at_utc)
    ON public.protected_event_payloads TO :"runtime_role";
GRANT INSERT (
    event_id, case_id, aggregate_version, report_revision, command_id,
    actor_id, event_type, schema_version, occurred_at_utc, recorded_at_utc, payload_id
) ON public.case_events TO :"runtime_role";
GRANT INSERT (
    receipt_id, actor_id, command_id, case_id, command_type, schema_version,
    canonical_payload_hash, first_aggregate_version, last_aggregate_version,
    report_revision, accepted_at_utc
) ON public.command_receipts TO :"runtime_role";
GRANT INSERT (
    outbox_id, event_id, message_type, created_at_utc, available_at_utc,
    attempt_count, lease_until_utc, published_at_utc
) ON public.domain_outbox TO :"runtime_role";
GRANT INSERT (
    audit_id, actor_id, target_actor_id, resource_id, operation,
    decision_code, access_epoch, recorded_at_utc, reason_code
) ON public.security_audit_entries TO :"runtime_role";

-- Recheck effective privileges after grants, including PUBLIC and inherited
-- privileges. History remains insert-only; outbox delivery is a separate role.
WITH grant_contract(relation_name, update_columns, insert_columns) AS (
    VALUES
        ('organizations', ARRAY[]::text[], ARRAY[]::text[]),
        ('workspaces', ARRAY[]::text[], ARRAY[]::text[]),
        ('workspace_organizations', ARRAY[]::text[], ARRAY[]::text[]),
        ('actor_access_states', ARRAY['access_epoch', 'version', 'updated_at_utc']::text[], ARRAY[]::text[]),
        ('actors', ARRAY[]::text[], ARRAY[]::text[]),
        ('organization_memberships', ARRAY[]::text[], ARRAY[]::text[]),
        ('access_grant_groups', ARRAY[]::text[], ARRAY[]::text[]),
        ('access_grant_group_regions', ARRAY[]::text[], ARRAY[]::text[]),
        ('access_grant_group_operations', ARRAY[]::text[], ARRAY[]::text[]),
        ('case_shares', ARRAY[]::text[], ARRAY[]::text[]),
        ('managed_cases', ARRAY['aggregate_version', 'report_revision', 'resource_version', 'workflow', 'workflow_version', 'accountable_owner_actor_id', 'updated_at_utc']::text[], ARRAY[]::text[]),
        ('managed_case_regions', ARRAY[]::text[], ARRAY[]::text[]),
        ('protected_event_payloads', ARRAY[]::text[], ARRAY['payload_id', 'protected_bytes', 'protection_purpose_version', 'created_at_utc']::text[]),
        ('case_events', ARRAY[]::text[], ARRAY['event_id', 'case_id', 'aggregate_version', 'report_revision', 'command_id', 'actor_id', 'event_type', 'schema_version', 'occurred_at_utc', 'recorded_at_utc', 'payload_id']::text[]),
        ('command_receipts', ARRAY[]::text[], ARRAY['receipt_id', 'actor_id', 'command_id', 'case_id', 'command_type', 'schema_version', 'canonical_payload_hash', 'first_aggregate_version', 'last_aggregate_version', 'report_revision', 'accepted_at_utc']::text[]),
        ('domain_outbox', ARRAY[]::text[], ARRAY['outbox_id', 'event_id', 'message_type', 'created_at_utc', 'available_at_utc', 'attempt_count', 'lease_until_utc', 'published_at_utc']::text[]),
        ('security_audit_entries', ARRAY[]::text[], ARRAY['audit_id', 'actor_id', 'target_actor_id', 'resource_id', 'operation', 'decision_code', 'access_epoch', 'recorded_at_utc', 'reason_code']::text[])
), effective_relations AS (
    SELECT contract.relation_name,
           contract.update_columns,
           contract.insert_columns,
           'public.' || contract.relation_name AS qualified_name,
           relation.oid AS relation_id
      FROM grant_contract AS contract
      JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = 'public'
      JOIN pg_catalog.pg_class AS relation
        ON relation.relnamespace = namespace.oid
       AND relation.relname = contract.relation_name
)
SELECT NOT EXISTS (
    SELECT 1
      FROM effective_relations AS target
     WHERE pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'INSERT')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'UPDATE')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'DELETE')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'TRUNCATE')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'REFERENCES')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'TRIGGER')
        OR pg_catalog.has_table_privilege(:'runtime_role', target.qualified_name, 'MAINTAIN')
        OR EXISTS (
            SELECT 1
              FROM pg_catalog.pg_attribute AS attribute
             WHERE attribute.attrelid = target.relation_id
               AND attribute.attnum > 0
               AND NOT attribute.attisdropped
               AND (
                   (pg_catalog.has_column_privilege(:'runtime_role', target.relation_id, attribute.attname, 'UPDATE')
                    AND NOT attribute.attname = ANY (target.update_columns))
                   OR (pg_catalog.has_column_privilege(:'runtime_role', target.relation_id, attribute.attname, 'INSERT')
                    AND NOT attribute.attname = ANY (target.insert_columns))
                   OR pg_catalog.has_column_privilege(:'runtime_role', target.relation_id, attribute.attname, 'REFERENCES')
               )
        )
) AND NOT EXISTS (
    SELECT 1
      FROM (VALUES
          ('actor_access_states', 'access_epoch'),
          ('actor_access_states', 'version'),
          ('actor_access_states', 'updated_at_utc'),
          ('managed_cases', 'aggregate_version'),
          ('managed_cases', 'report_revision'),
          ('managed_cases', 'resource_version'),
          ('managed_cases', 'workflow'),
          ('managed_cases', 'workflow_version'),
          ('managed_cases', 'accountable_owner_actor_id'),
          ('managed_cases', 'updated_at_utc')
      ) AS required(relation_name, column_name)
      JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = 'public'
      JOIN pg_catalog.pg_class AS relation
        ON relation.relnamespace = namespace.oid AND relation.relname = required.relation_name
     WHERE NOT pg_catalog.has_column_privilege(:'runtime_role', relation.oid, required.column_name, 'UPDATE')
) AND NOT EXISTS (
    SELECT 1
      FROM (VALUES
          ('protected_event_payloads', 'payload_id'),
          ('protected_event_payloads', 'protected_bytes'),
          ('protected_event_payloads', 'protection_purpose_version'),
          ('protected_event_payloads', 'created_at_utc'),
          ('case_events', 'event_id'), ('case_events', 'case_id'),
          ('case_events', 'aggregate_version'), ('case_events', 'report_revision'),
          ('case_events', 'command_id'), ('case_events', 'actor_id'),
          ('case_events', 'event_type'), ('case_events', 'schema_version'),
          ('case_events', 'occurred_at_utc'), ('case_events', 'recorded_at_utc'), ('case_events', 'payload_id'),
          ('command_receipts', 'receipt_id'), ('command_receipts', 'actor_id'),
          ('command_receipts', 'command_id'), ('command_receipts', 'case_id'),
          ('command_receipts', 'command_type'), ('command_receipts', 'schema_version'),
          ('command_receipts', 'canonical_payload_hash'),
          ('command_receipts', 'first_aggregate_version'), ('command_receipts', 'last_aggregate_version'),
          ('command_receipts', 'report_revision'), ('command_receipts', 'accepted_at_utc'),
          ('domain_outbox', 'outbox_id'), ('domain_outbox', 'event_id'),
          ('domain_outbox', 'message_type'), ('domain_outbox', 'created_at_utc'),
          ('domain_outbox', 'available_at_utc'), ('domain_outbox', 'attempt_count'),
          ('domain_outbox', 'lease_until_utc'), ('domain_outbox', 'published_at_utc'),
          ('security_audit_entries', 'audit_id'), ('security_audit_entries', 'actor_id'),
          ('security_audit_entries', 'target_actor_id'), ('security_audit_entries', 'resource_id'),
          ('security_audit_entries', 'operation'), ('security_audit_entries', 'decision_code'),
          ('security_audit_entries', 'access_epoch'), ('security_audit_entries', 'recorded_at_utc'),
          ('security_audit_entries', 'reason_code')
      ) AS required(relation_name, column_name)
      JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = 'public'
      JOIN pg_catalog.pg_class AS relation
        ON relation.relnamespace = namespace.oid AND relation.relname = required.relation_name
     WHERE NOT pg_catalog.has_column_privilege(:'runtime_role', relation.oid, required.column_name, 'INSERT')
) AND pg_catalog.has_table_privilege(:'runtime_role', 'public.protected_event_payloads', 'SELECT')
   AND pg_catalog.has_table_privilege(:'runtime_role', 'public.case_events', 'SELECT')
   AND pg_catalog.has_table_privilege(:'runtime_role', 'public.command_receipts', 'SELECT')
   AND (
       WITH select_allowlist(relation_name) AS (
           VALUES
               ('organizations'),
               ('workspaces'),
               ('workspace_organizations'),
               ('actor_access_states'),
               ('actors'),
               ('organization_memberships'),
               ('access_grant_groups'),
               ('access_grant_group_regions'),
               ('access_grant_group_operations'),
               ('case_shares'),
               ('managed_cases'),
               ('managed_case_regions'),
               ('protected_event_payloads'),
               ('case_events'),
               ('command_receipts')
       ), application_relations AS (
           SELECT relation.oid, relation.relname
             FROM pg_catalog.pg_class AS relation
             JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'public'
              AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND NOT EXISTS (
                  SELECT 1
                    FROM pg_catalog.pg_depend AS extension_membership
                   WHERE extension_membership.classid = 'pg_catalog.pg_class'::regclass
                     AND extension_membership.objid = relation.oid
                     AND extension_membership.refclassid = 'pg_catalog.pg_extension'::regclass
                     AND extension_membership.deptype = 'e'
              )
       )
       SELECT NOT EXISTS (
           SELECT 1
             FROM select_allowlist AS required
             JOIN pg_catalog.pg_namespace AS namespace ON namespace.nspname = 'public'
             JOIN pg_catalog.pg_class AS relation
               ON relation.relnamespace = namespace.oid
              AND relation.relname = required.relation_name
            WHERE NOT pg_catalog.has_table_privilege(:'runtime_role', relation.oid, 'SELECT')
       ) AND NOT EXISTS (
           SELECT 1
             FROM application_relations AS target
            WHERE NOT EXISTS (
                SELECT 1 FROM select_allowlist AS allowed WHERE allowed.relation_name = target.relname
            )
              AND (
                  pg_catalog.has_table_privilege(:'runtime_role', target.oid, 'SELECT')
                  OR EXISTS (
                      SELECT 1
                        FROM pg_catalog.pg_attribute AS attribute
                       WHERE attribute.attrelid = target.oid
                         AND attribute.attnum > 0
                         AND NOT attribute.attisdropped
                         AND pg_catalog.has_column_privilege(:'runtime_role', target.oid, attribute.attname, 'SELECT')
                  )
              )
       ) AND NOT EXISTS (
           SELECT 1
             FROM pg_catalog.pg_class AS sequence
             JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = sequence.relnamespace
            WHERE namespace.nspname = 'public'
              AND sequence.relkind = 'S'
              AND NOT EXISTS (
                  SELECT 1
                    FROM pg_catalog.pg_depend AS extension_membership
                   WHERE extension_membership.classid = 'pg_catalog.pg_class'::regclass
                     AND extension_membership.objid = sequence.oid
                     AND extension_membership.refclassid = 'pg_catalog.pg_extension'::regclass
                     AND extension_membership.deptype = 'e'
              )
              AND (
                  pg_catalog.has_sequence_privilege(:'runtime_role', sequence.oid, 'SELECT')
                  OR pg_catalog.has_sequence_privilege(:'runtime_role', sequence.oid, 'USAGE')
                  OR pg_catalog.has_sequence_privilege(:'runtime_role', sequence.oid, 'UPDATE')
              )
       )
   )   AND pg_catalog.has_function_privilege(:'runtime_role', 'swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)', 'EXECUTE')
   AND NOT pg_catalog.has_schema_privilege(:'runtime_role', 'public', 'CREATE')
   AND NOT pg_catalog.has_schema_privilege(:'runtime_role', 'swiftaid_internal', 'CREATE')
   AS runtime_writer_acl_valid \gset

\if :runtime_writer_acl_valid
    COMMIT;
\else
    \echo 'Runtime case-command ACL validation failed; transaction will be rolled back.'
    ROLLBACK;
    DO $$ BEGIN RAISE EXCEPTION 'SwiftAid runtime-grants postcondition failed.' USING ERRCODE = 'P0001'; END $$;
\endif
