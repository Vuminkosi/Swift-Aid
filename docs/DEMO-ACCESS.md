# Signed-in demo access

Local Compose enables `DemoAccess:Enabled` in Development. Other environments
reject this setting at startup. Anonymous requests do not provision grants.

On an authenticated request, the provisioner creates explicit PostgreSQL membership
and operation grants in `org-demo-1` / `workspace-demo-1`, using the scopes present
in the synthetic dataset. Grants last 30 days. Cases without regions receive
explicit shares; other cases use jurisdiction grants covering their demo regions.
Existing suspended/revoked memberships, grants and shares are not reactivated.
Other organizations and workspaces are unchanged. No schema migration is needed.

Refresh the page after deployment and start a new Ellai session so its scope includes
the new grants. Existing session snapshots are not rewritten. This enables the
implemented case operations; provider configuration, human approvals and excluded
clinical capabilities are separate from access grants.

The session scope reader works without a command lease, reads a consistent database
snapshot, and preserves each shared case's operation subset and expiry. Case commands
continue to use the transaction-bound authorization store.

Acceptance checks: lease-free assigned scope, stale epoch rejection, shared operation
and expiry correlation, revocation, cross-workspace isolation, expired shares,
disabled provisioning, idempotent demo provisioning and Production rejection.
These run in `SessionScopePostgresTests` against a disposable PostgreSQL database.

Use a dedicated demo database. Turning off provisioning stops new grants; it does
not revoke persisted grants. Existing grants expire or can be revoked through the
normal authority administration path. Never reuse this demo database for production.
