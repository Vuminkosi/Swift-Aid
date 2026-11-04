# SW-01-006 durability and replay audit - 2 November 2026

## Outcome

Source inventory is partial; this checklist requirement is broader than any single currently mapped subsystem. Managed case commands have stable command IDs, persisted receipts, expected aggregate-version checks and transactional persistence. Several independent outboxes and worker paths exist, including ingestion delivery. The source tree does not yet have a completed end-to-end catalog proving every listed domain/job consumer has durable inbox deduplication, lease fencing, ACK-after-commit, dead-letter handling and reconciliation. This audit does not claim those properties globally.

## Evidence found

| Requirement | Source evidence | Scope limit |
|---|---|---|
| Stable command identity and replay | `src/SwiftAid.Contracts/Commands/CommandEnvelope.cs`; `src/SwiftAid.Infrastructure/Persistence/Processing/PostgresCaseCommandPersistence.cs`; command receipt unique constraints in `src/SwiftAid.Infrastructure/Persistence/SwiftAidDbContext.cs` | Managed case command path only. Receipt behavior depends on database transaction/authorization paths; this is not a cross-subsystem replay proof. |
| Optimistic concurrency | `CaseCommandProcessor.cs`; `PostgresCaseCommandPersistence.cs` | Expected aggregate version is mapped and checked in the managed case command path. Other aggregates are outside this audit. |
| Transactional outbox | `src/SwiftAid.Infrastructure/Persistence/Processing/PostgresIngestionOutbox.cs`; `src/SwiftAid.Infrastructure/Persistence/SwiftAidDbContext.cs`; notification/classification/Ellai outbox entities and dispatchers | Multiple purpose-specific outboxes exist. Each needs separate transaction, idempotency, publish confirmation and recovery evidence. |
| Claim leases/fencing | `src/SwiftAid.Gateway/Ingestion/IngestionOutboxWorker.cs`; persistence and worker lease contracts in `src/SwiftAid.Application/` and `src/SwiftAid.Infrastructure/` | Ingestion worker documents database fencing; classification, notifications, Ellai and other workers require independent proof. |
| Worker inbox / deduplication | Worker-specific registries, receipts and result records exist in classification/translation paths. Generic Python `jobs/consumer.py` validates envelope shape. | Envelope validation is not a durable inbox or job execution. No universal worker inbox/ACK-after-commit proof was established. |
| Dead letters/reconciliation | Purpose-specific retry/attempt fields and reconciliation-oriented flows exist. | No comprehensive dead-letter and reconciliation matrix was established for all event/job/outbox lanes. |

## Required closure work

Create a lane-by-lane matrix for each command, event, outbox and worker job: transaction boundary, idempotency key, persisted inbox/receipt, lease owner/token/expiry, stale-owner rejection, publish/ACK ordering, bounded retry, terminal/dead-letter state, operator reconciliation and exact focused test evidence. Trace each publisher and consumer before changing shared schemas. Preserve each product boundary: PostgreSQL authoritative data; Qdrant derived/private; workers do not author privileged case records.

No source, schema, migration or test was changed or run for this audit. Relevant recent focused tests in the repository evidence are historical scoped results, not a substitute for the matrix above. Current contract/database ownership handoff remains unresolved (BL-005), so implementation is deferred until the missing lane inventory identifies safe, owned slices.


## Session-reminder lane update - 3 November 2026

The separate session-reminder recipient/channel outbox has now been implemented and verified at a focused lane scope. It uses a unique occurrence/account/subscription-version/channel key, a bounded PostgreSQL claim with lease fencing, exact-version consent/account/session rechecks before routing, and transactionally invalidates pending claims on session revision/cancellation. Interrupted in-flight or ambiguous provider outcomes are held as OutcomeUnknown rather than automatically resent; retry is bounded to safe preflight/configuration failures. Evidence: EV-082 and EV-083. Application dispatcher/service tests pass 14/14; fresh PostGIS SessionSchedulingTests pass 12/12; Gateway and Integration builds pass; EF reports no pending model changes. Provider receipt/reconciliation, deployed worker execution, and browser/device workflows remain NOT VERIFIED. This closes only a bounded session-reminder lane slice; no generic inbox, ACK-after-commit, dead-letter or reconciliation conclusion is inferred for other lanes. SW-01-006 remains partial.

## Ingestion outbox lane verification - 3 November 2026

PASS: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~IngestionOutboxPostgresTests` passed 6/6 with zero skips/failures. A task-created loopback PostGIS 17 database and unauthenticated NATS 2.15.1 JetStream container were used; both were removed. Coverage includes disjoint concurrent claims, expired-owner fencing, bounded retry and terminal exhaustion, case-revision predecessor ordering, provider failure remaining unpublished, and real JetStream PubAck followed by simulated process loss before database completion. Replay reused the same stream sequence and the database row completed once. This verifies the tested ingestion lanes only; broader job-consumer ACK-after-commit, dead-letter, and operator reconciliation coverage remains open.