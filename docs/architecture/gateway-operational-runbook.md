# Gateway ingestion operational runbook

## Rollout

1. Keep `SWIFTAID_INGESTION_DISPATCH_ENABLED=false` while deploying the additive `IngestionOutboxDelivery` migration using the existing database migration procedure. Pause any external publisher of these tables. The migration invalidates old ownerless leases and retains every row. Index replacement takes database locks; schedule accordingly for large tables.
2. Recreate NATS with the updated `infra/nats.conf` ACL; preserve its data volume. The bootstrap identity can create/read only the named stream, not update or purge it. Gateway can publish only the two ingestion subjects and query stream information.
3. Run `docker compose -f infra/compose.yml --profile ingestion run --rm nats-ingestion-bootstrap`. Existing configuration must match; mismatches fail without changes. Local storage is bounded and rejects new publication when full.
4. Set `SWIFTAID_INGESTION_DISPATCH_ENABLED=true` and recreate Gateway after its new image is built. Enabling publication does **not** enable intelligence processing. Plan backlog capacity and durable consumer deployment before enabling a large historical backlog.
5. Observe pending/exhausted counts, oldest pending age and `SwiftAid.Ingestion` instruments. Verify published timestamps advance and retain old rows for reconciliation.

No automatic shared-stack restart was performed by the implementation tests. See the performance report for isolated validation.

## Inspect delivery without reading report facts

```sql
SELECT count(*) FILTER (WHERE published_at_utc IS NULL AND exhausted_at_utc IS NULL) AS pending,
       count(*) FILTER (WHERE exhausted_at_utc IS NOT NULL) AS exhausted,
       min(created_at_utc) FILTER (WHERE published_at_utc IS NULL) AS oldest_unpublished
FROM swiftaid_intake.intake_outbox;

SELECT outbox_id, event_id, attempt_count, last_failure_code, exhausted_at_utc
FROM domain_outbox WHERE exhausted_at_utc IS NOT NULL
ORDER BY exhausted_at_utc, outbox_id LIMIT 100;
```

Use the analogous intake query with `attempts` and `receipt_id`. Never log protected payloads or receipt proofs. Broker storage exhaustion and unavailable/misconfigured streams cause bounded retries, then retained exhausted rows; acceptance transactions remain independent.

## Deliberate requeue

Resolve the underlying failure first. Use a privileged operational database connection and record the operator, exact row IDs, reason and incident reference in the operational audit. Requeue only the reviewed row; preserve its original outbox/event identity. Example uses a parameter supplied by the database client:

```sql
UPDATE domain_outbox
SET attempt_count = 0, available_at_utc = CURRENT_TIMESTAMP,
    exhausted_at_utc = NULL, last_failure_code = NULL
WHERE outbox_id = :reviewed_outbox_id
  AND published_at_utc IS NULL AND exhausted_at_utc IS NOT NULL
  AND lease_id IS NULL AND lease_until_utc IS NULL;
```

For intake, use `swiftaid_intake.intake_outbox` and `attempts`. Check exactly one affected row. Failed earlier case revisions block later ones until repaired. Do not change accepted source facts or invent a new event ID to evade replay detection. There is not yet a self-service requeue API or automatic operator audit endpoint.

## Failure and recovery expectations

- Gateway loss before/after publish: an expired lease is reclaimed with a new owner token and the same message ID.
- PubAck followed by database failure: retry is accepted as duplicate storage during the broker window. Beyond it, duplicate delivery is possible and requires consumer database idempotency.
- Old publisher wakes up: database completion/failure is rejected after lease expiry or replacement.
- Final attempt crashes: a bounded sweep records `lease-expired` exhaustion.
- Broker outage/full stream: report capture still commits; delivery retries/exhausts visibly. Never mark rows published manually to clear a backlog.
- Consumer restart/replay, poison processing, media reconciliation and late-data detection: not implemented by the dispatch slice. Do not purge retained messages based on publication timestamps.
- Rollback: disable dispatch first. Prefer fix-forward. Dropping the additive columns loses delivery diagnostics and requires deliberate operator review.

## Shared workspace rollout and links

Apply `20261101061649_SharedOrganizationWorkspaces` before deploying the updated application. It backfills each original organization link, adds the optional import organization and retargets the existing case/grant composite foreign keys. It does not move or share existing cases. The migration also installs a fixed-search-path, PUBLIC-revoked lock function and a link-change trigger. Existing restricted case readers need SELECT on `workspace_organizations`; registration runtimes additionally need EXECUTE on `swiftaid_internal.lock_workspace_organization(text,text)`. Run the reviewed role provisioning scripts with a privileged deployment identity, never the application login.

Only a privileged database operator may add or change a link. The application case-writer profile has no link writes. Example, using a separately configured secure PostgreSQL connection (no credentials in the command):

```powershell
psql -v workspace_id=YOUR_WORKSPACE -v organization_id=YOUR_ORGANIZATION -v operator_actor_id=YOUR_REGISTERED_OPERATOR -v link_status=Active -v change_reference=YOUR_CHANGE_REFERENCE -f infra/postgres/workspace-organization-link.sql
```

Use `Suspended` or `Revoked` to disable participation while retaining historical references. The script uses one transaction, a bounded lock wait and the existing security audit ledger, including the database operator, IDs and change reference. It requires active workspace/organization resources and a registered active operator. It does not create memberships, access grants, case shares or approval receipts. Concurrent case work finishes before revocation commits; subsequent checks deny the revoked link. Existing cases keep their original organization. A workspace's legacy/default organization must not be changed as a shortcut for relinking historical data.

The original organization is retained for old-client requests that omit `organizationId`. New imports explicitly select their organization. Adding secondary links is a forward migration: the downgrade guard refuses to remove a populated shared-workspace relationship.

### Explicit process-loss fixture

`IngestionRestartTests` requires `SWIFTAID_RUN_INGESTION_RESTART=1` **in addition** to the normal intake fixture opt-in. It refuses targets other than the named `swiftaid-ingestion-test-pg` (127.0.0.1:55439, database `swiftaid_intake_test`) and `swiftaid-ingestion-test-nats` (127.0.0.1:44229) containers, and requires their `swiftaid.task` label to begin `ingestion-`. It sends SIGKILL and starts those disposable containers again. Never label shared services to satisfy this guard. Run it serially with no other work using those fixtures. Provision migrations and the existing replay helper before running; the intake fixture applies pending migrations but does not install that separately reviewed helper.

The test uses real HTTP import routes with synthetic identity/policy and real database/broker storage. It retains source IDs, receipts and outbox records across process loss and asserts duplicate PubAck sequence stability. It does not prove whole-host durability or replica failover.

## Isolated tests

### Import measurements

Subscribe a .NET/OpenTelemetry metrics reader to meter `SwiftAid.Ingestion.Imports`. `swiftaid.ingestion.import.duration` is a millisecond histogram with the bounded `stage` label: batch, admission, validation, registration and checkpoint. `swiftaid.ingestion.import.rows` counts durably checkpointed row attempts by bounded `status` (Accepted, Replayed, Invalid, Denied, Conflict, RetryableFailure, Failed). Replays/retries are attempts, not new case counts. Use the canonical receipt/case ledger for unique accepted cases.

No actor, organization, workspace, import, case identifier or source text is a metric label. Listener failure cannot change the durable application result. The instrumentation was observed with a real MeterListener during PostgreSQL registration and replay; no telemetry backend/exporter or production dashboard is configured or claimed by this slice.

### Restricted import runtime

For a **new, dedicated restricted case/import database login**, apply `case-authority-lock-helper.sql` with its existing NOLOGIN helper owner, then `case-command-runtime-grants.sql`, then `case-import-runtime-grants.sql`. Supply the existing `runtime_role` to each script; none creates credentials. The import extension grants the canonical registration inserts (including its notification/outbox transaction), the bounded workspace lock function and import ledger access. Immutable source/context columns cannot be updated, and the application cannot write organization links/memberships or mark outbox rows published. The migration/deployment identity remains separate.

The base case-command profile intentionally rejects registration INSERT rights: do not reapply that narrower profile to an already extended import role. Reapply the import extension directly; it verifies its critical authority/source invariants and is idempotent. Review any subsequent privilege changes explicitly. This profile covers the registration/import workflow, not every Gateway identity, media, notification-delivery or ingestion-dispatch function; those services need their reviewed profiles rather than broad database-owner credentials.

`CaseImportRuntimeTests` uses a separately authenticated non-owner database connection to the same disposable fixture. Set `SWIFTAID_TEST_IMPORT_RUNTIME_CONNECTION` in addition to the normal intake fixture variables. The test proves accepted registration, replay, one receipt/two events, and denied authority/source/outbox-state mutations. Its first run failed on the missing lock-function privilege; after the complete registration profile it passed. PostgreSQL failure diagnostics are confined to synthetic local fixtures.

Set `SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES=1` and `SWIFTAID_TEST_INTAKE_POSTGRES_CONNECTION` to a disposable loopback database named `swiftaid_intake_test`. The tests delete outbox fixture rows. Set `SWIFTAID_TEST_INGESTION_NATS_URL=nats://127.0.0.1:44229` to a disposable broker for the crash/replay case. Never point these at shared data.

```powershell
dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~IngestionOutbox|FullyQualifiedName~JetStreamAcknowledgement|FullyQualifiedName~JobPublisher'
```
