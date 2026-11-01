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

## Isolated tests

Set `SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES=1` and `SWIFTAID_TEST_INTAKE_POSTGRES_CONNECTION` to a disposable loopback database named `swiftaid_intake_test`. The tests delete outbox fixture rows. Set `SWIFTAID_TEST_INGESTION_NATS_URL=nats://127.0.0.1:44229` to a disposable broker for the crash/replay case. Never point these at shared data.

```powershell
dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~IngestionOutbox|FullyQualifiedName~JetStreamAcknowledgement|FullyQualifiedName~JobPublisher'
```
