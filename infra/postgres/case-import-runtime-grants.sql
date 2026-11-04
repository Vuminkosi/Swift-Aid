-- Add canonical case registration and immutable-source import ledger rights to a case-writer profile.
-- Run case-authority-lock-helper.sql and case-command-runtime-grants.sql first.
-- Operator-only, idempotent, exact targets; no role creation, DELETE, TRUNCATE or DDL grants.
\set ON_ERROR_STOP on
\if :{?runtime_role}
\else
    \echo 'Required psql variable runtime_role is missing.'
    DO $$ BEGIN RAISE EXCEPTION 'Import runtime-grants preflight failed.'; END $$;
\endif

SELECT EXISTS (
    SELECT 1 FROM pg_catalog.pg_roles role
    WHERE role.rolname = :'runtime_role' AND role.rolcanlogin
      AND NOT role.rolsuper AND NOT role.rolcreatedb AND NOT role.rolcreaterole
      AND NOT role.rolreplication AND NOT role.rolbypassrls
      AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members member WHERE member.member = role.oid OR member.roleid = role.oid)
      AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_database database WHERE database.datname = current_database() AND database.datdba = role.oid)
      AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace scope WHERE scope.nspname IN ('public','swiftaid_internal','swiftaid_ingestion','swiftaid_notifications') AND scope.nspowner = role.oid)
      AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace scope ON scope.oid = relation.relnamespace
                      WHERE scope.nspname IN ('public','swiftaid_ingestion','swiftaid_notifications') AND relation.relowner = role.oid)
      AND pg_catalog.has_table_privilege(role.oid, 'public.actors', 'SELECT')
      AND pg_catalog.has_column_privilege(role.oid, 'public.command_receipts', 'receipt_id', 'INSERT')
      AND pg_catalog.has_function_privilege(role.oid, 'swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)', 'EXECUTE')
) AS import_role_valid \gset
\if :import_role_valid
\else
    \echo 'A restricted, already provisioned case-writer runtime role is required.'
    DO $$ BEGIN RAISE EXCEPTION 'Import runtime-grants preflight failed.'; END $$;
\endif

BEGIN;
GRANT EXECUTE ON FUNCTION swiftaid_internal.lock_workspace_organization(text,text) TO :"runtime_role";
GRANT INSERT (case_id, accountable_owner_actor_id, aggregate_version, classification, classification_version,
    country_code, created_at_utc, kind, laboratory, laboratory_version, organization_id, outcome_state,
    outcome_value, outcome_version, report_revision, resource_version, sector, sensitivity_code,
    updated_at_utc, workflow, workflow_version, workspace_id) ON public.managed_cases TO :"runtime_role";
GRANT USAGE ON SCHEMA swiftaid_notifications TO :"runtime_role";
GRANT INSERT (notification_id, occurrence_key, accepted_event_id, recipient_actor_id, kind, resource_kind,
    resource_id, action, recipient_requirement, created_at_utc, read_at_utc, dismissed_at_utc)
    ON swiftaid_notifications.notification_items TO :"runtime_role";
GRANT INSERT (outbox_id, notification_id, created_at_utc, available_at_utc, attempt_count, lease_id,
    lease_until_utc, published_at_utc, exhausted_at_utc)
    ON swiftaid_notifications.notification_delivery_outbox TO :"runtime_role";
GRANT USAGE ON SCHEMA swiftaid_ingestion TO :"runtime_role";
GRANT SELECT, INSERT ON swiftaid_ingestion.case_imports, swiftaid_ingestion.case_import_rows TO :"runtime_role";
GRANT UPDATE ("State", "NextRow", "FinalRow", "LeaseId", "LeaseUntilUtc", "ErrorCode", "UpdatedAtUtc")
    ON swiftaid_ingestion.case_imports TO :"runtime_role";
GRANT UPDATE ("State", "Attempts", "CaseId", "ErrorCode", "UpdatedAtUtc")
    ON swiftaid_ingestion.case_import_rows TO :"runtime_role";

SELECT NOT pg_catalog.has_schema_privilege(:'runtime_role', 'swiftaid_ingestion', 'CREATE')
   AND NOT pg_catalog.has_schema_privilege(:'runtime_role', 'swiftaid_notifications', 'CREATE')
   AND NOT pg_catalog.has_any_column_privilege(:'runtime_role', 'public.workspace_organizations', 'INSERT,UPDATE,REFERENCES')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'public.workspace_organizations', 'DELETE,TRUNCATE,TRIGGER')
   AND NOT pg_catalog.has_any_column_privilege(:'runtime_role', 'public.domain_outbox', 'UPDATE')
   AND NOT EXISTS (
       SELECT 1 FROM (VALUES ('organizations'), ('workspaces'), ('actors'), ('organization_memberships'),
           ('access_grant_groups'), ('access_grant_group_regions'), ('access_grant_group_operations'), ('case_shares')) authority(relation_name)
       WHERE pg_catalog.has_any_column_privilege(:'runtime_role', 'public.' || authority.relation_name, 'INSERT,UPDATE,REFERENCES')
          OR pg_catalog.has_table_privilege(:'runtime_role', 'public.' || authority.relation_name, 'DELETE,TRUNCATE,TRIGGER')
   )
   AND NOT EXISTS (
       SELECT 1 FROM (VALUES ('case_id'), ('organization_id'), ('workspace_id'), ('country_code'), ('sector'), ('sensitivity_code')) scope(column_name)
       WHERE pg_catalog.has_column_privilege(:'runtime_role', 'public.managed_cases', scope.column_name, 'UPDATE')
   )
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'swiftaid_ingestion.case_imports', 'DELETE,TRUNCATE,REFERENCES,TRIGGER')
   AND NOT pg_catalog.has_table_privilege(:'runtime_role', 'swiftaid_ingestion.case_import_rows', 'DELETE,TRUNCATE,REFERENCES,TRIGGER')
   AND NOT EXISTS (
       SELECT 1 FROM (VALUES
           ('case_imports','ImportId'), ('case_imports','ActorId'), ('case_imports','WorkspaceId'),
           ('case_imports','CountryCode'), ('case_imports','OrganizationId'), ('case_imports','ContextHash'), ('case_imports','CreatedAtUtc'),
           ('case_import_rows','ImportId'), ('case_import_rows','RowNumber'), ('case_import_rows','CommandId'),
           ('case_import_rows','SourceHash'), ('case_import_rows','ProtectedSource')
       ) immutable(relation_name, column_name)
       WHERE pg_catalog.has_column_privilege(:'runtime_role', 'swiftaid_ingestion.' || immutable.relation_name, immutable.column_name, 'UPDATE')
   ) AS import_acl_valid \gset
\if :import_acl_valid
    COMMIT;
\else
    ROLLBACK;
    DO $$ BEGIN RAISE EXCEPTION 'Import runtime-grants postcondition failed; existing privileges are broader than the reviewed profile.'; END $$;
\endif
