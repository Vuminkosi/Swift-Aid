# Gateway ingestion implementation plan

2026-11-01, two-hour delivery window beginning 05:12 UTC. Based on [the source audit](gateway-ingestion-audit.md). Full million-record surveillance infrastructure is larger than this window; each delivered slice must remain useful and independently verifiable.

## Slice order and acceptance

1. **Reliable dispatch (ING-01/02/03/05).** Retain existing intake/domain outboxes. Add lease identity, exhaustion/failure state and bounded claim/retry operations. Publish metadata-only notices with stable IDs; mark published only after the expected JetStream PubAck and only for the current unexpired lease. Claim disjoint work under concurrent publishers; preserve source revision ordering. Crashed final attempts become observable exhausted rows. Provide a deliberate reprocessing operation/runbook and bounded stream bootstrap. Test real PostgreSQL races, stale completion, retry/exhaustion, and broker ambiguity in disposable services.
2. **Bounded ingestion adapters (ING-06/07).** Retain the development demo importer. Add strict streaming/record-bounded import parsing with the existing string-array observations format, stable row identities and explicit validation failures. Route accepted cases through `IManagedCaseRegistrationProcessor` and public intake through `IIntakeService`, never direct case-table inserts. Persist per-record results/checkpoints if a runnable importer fits the window; do not call a parser a complete import system.
3. **Ellai ingestion assistance (ING-09).** Extend the current tool broker/catalogue with useful schema/validation/capture assistance. Reuse current session scope, source evidence, budgets, authorization, UI intents and exact approval receipts. Model output cannot self-approve registration or manufacture clinical facts. Add tests for guest/staff separation, invalid observations, retry identity and forbidden effects. Clearly identify whether the delivered tool prepares/validates or actually submits data.
4. **Processing continuation (ING-04/08).** Reuse existing job contracts and Python dependencies. Only enable a worker once durable result-before-ACK, Gateway-scoped authorization, idempotency, revision checks and poison handling exist. Classification/detection remain explicit later slices if prerequisites consume the window; no fabricated models, thresholds or readiness claims.
5. **Evidence and operations.** Maintain architecture/event/runbook/performance documents, focused regressions, synthetic load/fault harnesses and measured results. Run the largest justified disposable test within remaining time; report actual counts and untested scales. Commit verified slices on current `master`, never push.

## Invariants, rollout and failure handling

- No transport or voice rewrite; preserve Muse Spark, Muse transcription, Silero, MMS-TTS, Microsoft Agent Framework, notifications and Ellai outbox.
- PostgreSQL remains canonical. Transactions are per report/command; duplicate delivery cannot create duplicate side effects. Empty/failed provider output is never ingestion success.
- Broker messages contain identifiers/version metadata only; logs/metrics omit narratives, audio, credentials, proof tokens and precise locations.
- Migration is additive. Dispatch is explicitly enabled only after migration and stream provisioning; pre-existing backlog must remain recoverable. Do not restart shared production-like services for fault injection.
- Bounded batch size, payload size, attempt count, lease expiry and backoff apply. A terminal failure is retained and inspectable, never deleted as successful work.
- Current authorization is rechecked by canonical services. Receipt visibility does not become managed-case access. New tools do not bypass approval or access epoch checks.
- Source changes, schema tests, isolated integration tests and live user/device verification are separate evidence levels. No production-ready declaration without the required integrated scenarios and measured capacity.

Progress and exact delivered/unimplemented boundaries will be updated as slices complete.
