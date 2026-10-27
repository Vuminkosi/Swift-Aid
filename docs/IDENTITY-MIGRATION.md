# Identity migration recovery

This note covers migration `P13IdentityMapping`, which creates the `swiftaid_identity` schema and its identity, external-identity, and OpenIddict tables. The migration source includes a guard in `Down`: it checks all ten created tables and raises an exception if any contains rows. The expected error says the migration cannot be rolled back after identity data is written and directs operators to forward recovery or a verified-backup restore. This behavior is source-inspected; the disposable-database rollback test is pending and runtime verification is **NOT VERIFIED**.

## Before applying the migration

- Take a PostgreSQL backup using the approved operational procedure before applying the migration. Record its recovery point and verify that the backup can be restored into an isolated environment. A backup file without a successful restore check is not a verified backup.
- Confirm the restore plan includes the related PostgreSQL state and any required encryption or signing key material from its separately protected backup. Do not expose secrets in this record.
- Confirm the target database and migration revision with the release operator. Keep the verified backup available until post-migration checks are complete.

The exact backup and restore commands depend on the deployment environment and are not specified here. Use the approved platform runbook; do not improvise a production restore command.

## If migration application or verification fails

Do not attempt to run `Down` after identity or authorization data has been written. Its guard is designed to refuse that rollback; the expected exception is:

> P13IdentityMapping cannot be rolled back after identity data is written; use forward recovery or restore a verified backup.

Prefer a reviewed forward fix that preserves the new identity state, then verify the repaired schema and application behavior. Do not bypass or remove the guard to force a rollback.

A restore is an alternative only when the incident lead determines it is operationally appropriate and the backup is verified. Coordinate a consistent recovery point for PostgreSQL and dependent key material, account for writes made after that point, and follow the platform recovery procedure. Restoring an older database can discard later identity and application changes; do not restore only the identity schema in isolation unless the approved recovery plan explicitly supports it.

## Verification status

The guard and its ten-table scope are present in the migration source. A Docker-backed test of rollback on an empty schema and refusal after populating identity data is still pending. This note does not claim that the migration has been applied to a live environment or that a production backup/restore has been tested.
