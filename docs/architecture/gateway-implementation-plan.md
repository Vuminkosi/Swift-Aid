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

## Delivery status at the end of the bounded implementation slices

**PARTIAL against `designs/data/pipeline-requirements.md`.** The ingestion foundation is implemented and tested; the complete surveillance processing pipeline is not delivered. Accepted import rows are durable case registrations, not completed intelligence results.

| Requirement area | Delivered boundary | Remaining work |
|---|---|---|
| Audit and canonical design | Source audit, processing architecture, event contracts, runbook and measured performance report | Revisit findings as downstream workers become executable |
| Canonical ingestion | CSV adapter uses existing registration, validation, authorization, receipts and transactional case/outbox writes | JSONL, laboratory/backfill update adapters and broader media ingestion |
| Workspace imports | Streaming bounded batches, encrypted source ledger, per-row outcomes, pause/resume, original-command replay, owner-authorized history; shared-workspace organization selection | Autonomous background resume, complete file identity, broader native-client validation and workload-isolation benchmarks |
| Outbox and broker | Fenced leases, bounded retries, ordered revisions, duplicate PubAck correction; durable classification consumer with result-before-ACK, inbox, quarantine and derived lifecycle outbox | Other downstream consumers, automated retention and operator reprocessing UI |
| Processing and surveillance | Persistent classification registry, pinned real BART worker, source-language abstention, cache reuse and resumable revision-snapshot backfill | Quality, spatial/temporal detection, correlation, assessments, human verification and full-scale backfill validation |
| Ellai | Schema/observation/location assistance plus authorized destination, import-list, import-status and stored classification-evidence tools | Provider-driven capture acceptance and signal-triggered durable investigation orchestration |
| Observability | Bounded import stage/outcome metrics and durable inspectable failures | Exporter/backend setup, full distributed tracing, downstream metrics, dashboards and alerts |
| Resilience and scale | Real PostgreSQL/NATS process-loss recovery, source/authority isolation, restricted-role checks, browser resume and 15,000-row durable HTTP ingestion | Consumer/MinIO fault matrix, concurrent interactive load, 100k/million/multi-million full processing, HA and hardware/provider validation |

The [classification continuation](gateway-classification-implementation.md) implements the first durable processing result/checkpoint boundary and real-model consumer. Next are quality, detection, signals and investigations, scaled reliability tests and operational UI. Classification completion is not completion of those downstream stages.

Local commits on `master`: audit `36c2874`; dispatch `b85afa4`; CSV/Ellai preparation `af21afa`; import backend `dc953d7`; Workspace UI `f690055`; supplied-file measurement `f54151e`; shared workspaces `154697f`; restricted import role `7687d4d`; upload/dependency corrections `792be6d`; Ellai monitoring `0f9ec62`; metrics `cbf45a2`; process-loss tests `118e9ee`; membership fixture regression `62608eb`. No commits were pushed. Deployment requires the reviewed migrations and runtime grants in the runbook. The shared Gateway/database were not replaced or migrated by this delivery.

Owner-directed scope addition: imports belong in Workspace, and multiple organizations may participate in one workspace. Preserve a single organization per case and current membership/grant boundaries. Implement explicit participation links, backwards-compatible optional organization selection for registration/imports, migration backfill, revocation locking and actor-owned organization-filtered history. Verify cross-organization denial, replay conflicts and the real browser selection workflow; do not infer new self-service organization-administration permissions.

Slice 1 implemented and verified: additive leased dispatch for both original outboxes, duplicate-PubAck correction for existing publishers, bounded opt-in stream provisioning and operations documentation. Focused final check: 28 passed, zero skipped, plus real ACL bootstrap and Compose validation. Consumer completion and intelligence stages remain separate outstanding work.

Next independently verified slice: strict bounded CSV adapter compatible with all 15,000 supplied records; shared source-attributed report mapping; Ellai schema, observation-format validation and public geographic lookup tools. These tools do not bypass human submission/approvals. See [format details](gateway-import-format.md).

Durable import backend delivered in `dc953d7`: encrypted source rows, live owner/workspace authorization, lease fencing, canonical registration and original-command replay after a committed case/checkpoint failure. Six initial PostgreSQL/HTTP integration tests passed. Workspace UI then adds file selection, bounded batches, pause/resume, restored counts and sanitized row issues. The Chromium fixture exercises the real Gateway component/application service and real PostgreSQL; identity and policy remain synthetic. Full intelligence consumers and sustained capacity remain separately tracked outstanding work.
