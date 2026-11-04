# Gateway ingestion audit

Audit date: 2026-11-01. Baseline: `08b1b2ee7410dc4a342d8127a62c01f343e16a45`, `master`.
Scope: `designs/data/pipeline-requirements.md`, including useful Ellai ingestion and capture tools. This is source evidence, not a production capacity assessment.

## Verified flow and inventory

```text
Guest web/mobile draft -> GuestSubmissionCoordinator -> PublicIntakeEndpoints
  -> IntakeService -> PostgresIntakeSubmissionStore
  -> transaction: protected report + attachment binding + intake_outbox -> receipt

Staff/offline command -> ManagedCaseRegistrationEndpoints / command endpoints
  -> CaseRegistrationProcessor / case command processor
  -> authorized unit of work -> events + projection + receipt + domain_outbox

Both ingestion outboxes -> [runtime dispatcher missing at baseline]
  -> NATS JetStream -> [processing consumers missing at baseline]
```

PostgreSQL is authoritative. ASP.NET Core owns identity, authorization, receipts, intake review and case commands. MinIO holds private originals. Geography uses registry identifiers; the inspected infrastructure has no implemented PostGIS spatial detector. Qdrant is a separate derived memory index. Redis is already configured for other features, not a proposed ingestion dependency. Compose contains PostgreSQL/PostGIS, NATS, MinIO, Qdrant, Redis, Gateway and optional MMS-TTS; no Python processing service. Python dependencies already include nats-py, httpx and Pydantic.

## Findings

| ID | Priority | Evidence and reproducible boundary | Consequence / correction |
|---|---|---|---|
| ING-01 | P0 | `PostgresIntakeSubmissionStore.CommitAsync` creates `IntakeOutboxEntity`; case registration/command persistence creates `DomainOutboxEntity`. Searching usages excluding migrations finds producers and mappings, but no dispatcher/hosted registration. | Accepted data persists but downstream processing never starts. Add dispatch over these same tables, with PubAck-gated completion. |
| ING-02 | P1 | `IntakeOutboxEntityConfiguration` has attempts/published only. `SwiftAidDbContext.ConfigureCaseEvents` domain outbox mapping has lease expiry but no lease identity, exhaustion or last failure. | Multiple publishers cannot safely fence completion; no durable exhausted outcome. Add additive fencing, due time and bounded retry state, including crashed final attempts. |
| ING-03 | P1 | `JetStreamEventPublisher` serializes `CaseEventEnvelope` including narrative-bearing payload types. Existing jobs instead contain opaque references. | Do not connect this full-payload publisher to ingestion blindly. Publish a bounded metadata-only change notice under a distinct versioned subject contract. |
| ING-04 | P1 | `workers/src/swiftaid_workers/jobs/consumer.py` explicitly validates only; `__main__.py` refuses consumer startup. Job persistence files define contracts/transitions but no active job repository/consumer flow. | Envelope tests are not processing evidence. Durable result commit before ACK, worker authorization, stale revision handling and poison reconciliation remain implementation requirements. |
| ING-05 | P1 | `infra/nats.conf` and `bootstrap-notification-stream.sh` bootstrap notifications only; jobs/events streams have no corresponding bootstrap in Compose. | Enabling dispatch alone yields publish failures. Add bounded ingestion stream provisioning with least-privilege subjects and explicit opt-in rollout. |
| ING-06 | P1 | `DemoCaseImporter.LoadCsvAsync` reads the whole file, inserts demo cases directly and skips all work when any demo case exists. | This synthetic startup seeder is not a resumable production import. Retain its development-only gate; build import adapters over application services, not direct inserts. |
| ING-07 | P1 | No production bulk-import endpoint, durable per-row import state or streaming CSV adapter found. | Large import/restart/cancellation requirements are unimplemented. Preserve string-array `observations_json`; bound records and fields before materialization. |
| ING-08 | P1 | `SurveillanceMapAggregateQuery` is an authorized descriptive count query. Surveillance jobs are explicitly disabled in publisher/persistence validation. | No evidence of implemented syndrome definitions, statistical detection, signal correlation or verification pipeline. Do not describe counts or model prose as outbreak detection. |
| ING-09 | P2 | `EllaiToolBroker` exposes read tools; `AdminActionKind` permits three administrative actions, not report creation. | Add useful bounded ingestion tools through the existing broker; preserve human approval, source facts and normal case/intake services. Never infer write authorization from a model tool request. |
| ING-10 | P2 | `docs/TECHNICAL.md` contains old classifier/consent and environment claims that differ from current code. | Keep this audit and linked canonical processing docs explicit about source versus runtime evidence; do not restore obsolete gates from stale prose. |

## Reusable safeguards and remaining risks

- Intake submission holds a per-session advisory lock, rechecks capability/proof/quota, compares canonical hashes on retry, encrypts payloads and commits receipt/outbox/attachment bindings together. Guest receipts never grant staff access.
- Case registration and commands acquire current authority, use deterministic locks and stable command receipts, and stage events/projections/outbox in one transaction. Imports and Ellai must reuse these services.
- Notification outbox already demonstrates `FOR UPDATE SKIP LOCKED`, lease IDs, bounded attempts and fenced updates. Reuse its pattern without merging independent Ellai/notification delivery semantics.
- JetStream publishers already use stable message IDs and expected-stream PubAck validation. Broker deduplication has a finite window; it cannot replace consumer database idempotency.
- Private media downloads use opaque references, size checks and bounded streaming. Upload reservation/finalization has separate authority; complete orphan reconciliation, route changes and object-store failure injection remain unverified.
- Offline coordinators preserve stable command/submission IDs. Guest coordination loads a local queue into memory; this is not a server bulk-import implementation or million-record capacity claim.
- No authoritative processing checkpoint/status query exists for the new ingestion stages. Tenant ownership must be resolved in Gateway; broker IDs alone confer no access.
- Ellai has an independent PostgreSQL utterance outbox and existing approval receipts. Neither is being replaced by JetStream.
- Existing Ellai metrics do not establish ingestion queue-age, throughput or processing-latency coverage. No application OpenTelemetry exporter registration was found; instrumentation and export must be verified separately.

Follow-up fault injection confirmed another P1 defect: the installed NATS client throws for duplicate PubAck in `EnsureSuccess()`. All three existing publisher adapters used it. Valid duplicate storage acknowledgements now receive the same stream/sequence validation as first publication; the real broker replay regression verifies the correction.

## Audit checks and limits

PASS: read-only source/configuration searches across intake, registration, command persistence, outbox entities/mappings, NATS publishers/ACL/bootstrap/Compose, worker entry point/contracts, offline coordinators, media store, geography, aggregate queries, Ellai broker/actions, tests and CI. Git baseline was clean. Local Docker images include PostGIS 17 and NATS 2.15.1.

NOT VERIFIED during audit: provider behavior, device flows, high-volume throughput, failover, restore/replay, live worker processing, signal accuracy, production authorization deployment. Existing tests were inspected, not rerun as audit proof. Verification results belong in `gateway-performance-report.md` and must identify synthetic/disposable environments.

Priorities and retained services are specified in [the implementation plan](gateway-implementation-plan.md).
