-- Privileged operator workflow. This does not create membership or case-access grants.
-- psql -v workspace_id=... -v organization_id=... -v operator_actor_id=...
--      -v link_status=Active -v change_reference=... -f workspace-organization-link.sql
\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '10s';
-- Preserve the same organization -> workspace lock order used by authority leases.
SELECT organization_id FROM public.organizations WHERE organization_id = :'organization_id' FOR SHARE;
SELECT workspace_id FROM public.workspaces WHERE workspace_id = :'workspace_id' FOR UPDATE;
SELECT :'link_status' IN ('Active','Suspended','Revoked')
   AND length(btrim(:'change_reference')) BETWEEN 1 AND 200
   AND EXISTS (SELECT 1 FROM public.organizations WHERE organization_id = :'organization_id' AND status = 'Active')
   AND EXISTS (SELECT 1 FROM public.workspaces WHERE workspace_id = :'workspace_id' AND status = 'Active')
   AND EXISTS (SELECT 1 FROM public.actors actor JOIN public.actor_access_states state USING (actor_id)
               WHERE actor.actor_id = :'operator_actor_id' AND actor.status = 'Active'
                 AND (actor.expires_at_utc IS NULL OR actor.expires_at_utc > CURRENT_TIMESTAMP)) AS link_inputs_valid \gset
\if :link_inputs_valid
\else
    ROLLBACK;
    DO $$ BEGIN RAISE EXCEPTION 'Workspace link change requires active resources, a registered operator and a change reference.'; END $$;
\endif
INSERT INTO public.workspace_organizations (workspace_id, organization_id, status, created_at_utc, updated_at_utc)
VALUES (:'workspace_id', :'organization_id', :'link_status', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT (workspace_id, organization_id) DO UPDATE
SET status = EXCLUDED.status, updated_at_utc = EXCLUDED.updated_at_utc;
INSERT INTO public.security_audit_entries
    (audit_id, actor_id, operation, decision_code, access_epoch, recorded_at_utc, reason_code)
SELECT gen_random_uuid(), actor_id, 'WorkspaceOrganizationLink', :'link_status', access_epoch, CURRENT_TIMESTAMP,
       json_build_object('workspaceId', :'workspace_id', 'organizationId', :'organization_id',
                         'reference', :'change_reference', 'databaseOperator', CURRENT_USER)::text
FROM public.actor_access_states WHERE actor_id = :'operator_actor_id';
COMMIT;
