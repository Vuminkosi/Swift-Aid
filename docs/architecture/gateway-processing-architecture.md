# Gateway processing architecture

The subsequent [persistent classification vertical slice](gateway-classification-implementation.md) adds an opt-in durable Python consumer, scoped Gateway lease/result routes, PostgreSQL registry and classification lifecycle outbox lane. Its real-model integration test is recorded separately from the original dispatch evidence below. Downstream quality, detection, correlation and investigation remain outstanding.

## Implemented delivery boundary

Public intake and managed cases retain their existing transaction, authorization, encryption, receipt and event paths. The additive `IngestionOutboxDelivery` migration extends their existing outbox rows with delivery fencing and failure state. `PostgresIngestionOutbox` claims due work with `FOR UPDATE SKIP LOCKED`; it never decrypts report payloads.

`IngestionOutboxWorker` alternates intake and case lanes. Each attempt claims one row with a 60-second lease and a fresh random lease ID, then allows up to 15 seconds for publication. Multiple Gateway replicas may claim concurrently. Database updates require the exact lease ID and an unexpired lease. Failures retry with capped exponential backoff; attempt eight is terminal. A bounded sweep also exhausts rows whose last attempt died before recording failure.

`JetStreamIngestionNoticePublisher` emits a version-one opaque notice. A valid PubAck, including a duplicate PubAck from an earlier attempt, confirms storage. Completion also checks the exact stream and a positive sequence. A database failure after PubAck leaves the row pending for replay with its original message ID. This provides at-least-once publication, not exactly-once processing.

Unpublished earlier case aggregate versions prevent claiming later versions. Exhausted predecessors deliberately hold later versions for operator recovery. A stale network publication can still arrive after a newer one; future consumers must enforce source-version checks and database idempotency rather than relying on broker arrival order.

## Existing boundaries retained

- Ellai utterance processing remains on its own PostgreSQL outbox; voice transport and providers are unchanged.
- Notification delivery remains independent. Its publisher, the existing event publisher and the job publisher now also accept valid duplicate PubAck responses.
- `CaseRegistrationProcessor` and `IntakeService` remain the only application ingestion routes for their respective authority scopes. Public intake does not become a staff case automatically.
- Original media stays in private MinIO storage; source facts remain protected in PostgreSQL. No source narrative, precise location, transcript, receipt proof or actor identity is added to ingestion notices.
- Python job contracts remain available. The original baseline had no functioning durable consumer; the explicit classification consumer now binds a provisioned ingestion durable. Other legacy job types remain unchanged. Delivery publication alone does not enable downstream surveillance.

## Instrumentation and limits

`SwiftAid.Ingestion` exposes an `ActivitySource` and `Meter`: publication outcome counts, outbox age at dispatch and publication duration. Tags contain source kind and a closed outcome code; the trace contains only the opaque message ID. An OpenTelemetry subscriber/exporter still needs deployment wiring: emitting instruments is not evidence of an exported dashboard.

The local stream uses file storage, one replica, one million messages / 1 GiB, 4 KiB maximum message size and discard-new backpressure. It has no automatic age expiry. It is a bounded local configuration, not a production HA or million-record processing validation. Do not purge it until durable consumer completion/checkpoint reconciliation exists.

See [event contracts](gateway-event-contracts.md), [operations](gateway-operational-runbook.md) and [measured verification](gateway-performance-report.md).
