# SW-01-004 contract map - 2 November 2026

## Outcome

Source inventory for versioned commands, case events, outbox, worker jobs, AI tools, audio sessions, UI registry and public projections. This is a partial source map, not proof of complete coverage or runtime compatibility. The checklist remains open because the catalog does not yet connect every public contract, schema version, persistence representation, producer and consumer.

## Current contract surfaces

| Surface | Source locations | Observed contract and boundary |
|---|---|---|
| Managed case commands | `src/SwiftAid.Contracts/Commands/CommandEnvelope.cs`; `src/SwiftAid.Contracts/Cases/CaseCommands.cs`; `src/SwiftAid.Gateway/Endpoints/CaseCommandEndpoints.cs`; `src/SwiftAid.Application/Cases/Commands/` | Typed command envelopes carry schema version, command identity, case identity and expected aggregate version. Gateway command API declares supported wire schema v1 and maps accepted requests to application commands. PostgreSQL processing and receipts are implemented separately. |
| Case events | `src/SwiftAid.Contracts/Events/CaseEventEnvelope.cs`; `src/SwiftAid.Application/Serialization/CaseEventSerializer.cs`; `src/SwiftAid.Infrastructure/Persistence/Processing/CaseEventJournalReader.cs` | Typed event envelope has a schema version, event type and payload. Event vocabulary is an enum; serialized event payloads and protected payload storage have separate paths. |
| Client outbox | `src/SwiftAid.Client/Outbox/CommandOutbox.cs`; `src/SwiftAid.Client/Cases/CaseCommandOutboxEnvelope.cs`; `src/SwiftAid.Client/Cases/CaseCommandOutboxTransport.cs`; `src/SwiftAid.Client/Cases/OfflineCaseCommandClient.cs` | Offline command identity/payload is queued and later sent through the Gateway client. Expected aggregate version and replay/conflict behavior are explicit in the client flow. This map does not establish every offline command’s end-to-end replay behavior. |
| Gateway ingestion outbox | `src/SwiftAid.Application/Abstractions/IIngestionOutbox.cs`; `src/SwiftAid.Infrastructure/Persistence/Processing/PostgresIngestionOutbox.cs`; `src/SwiftAid.Gateway/Ingestion/IngestionOutboxWorker.cs` | Durable ingestion dispatch uses a separate outbox and worker. It is distinct from the case-command and classification outboxes. |
| Worker jobs | `src/SwiftAid.Contracts/Jobs/JobEnvelope.cs`; `workers/src/swiftaid_workers/jobs/consumer.py`; classification consumer under `workers/src/swiftaid_workers/classification/` | .NET envelope declares schema version 1; Python validates the same eight camelCase fields, seven `JobType` values, byte limit, UTC timestamp and canonical organization ID. Organization-ID rules are regression-tested across implementations. Generic `jobs/consumer.py` validates an envelope only; the classification worker has its own notice path. This does not claim general job execution/ACK behavior. |
| AI tools and plans | `src/SwiftAid.Application/Ellai/Tools/`; `src/SwiftAid.Application/Ellai/UiIntelligence/`; `src/SwiftAid.Contracts/Ellai/` | Tool broker mediates constrained actions; guidance drafts and admissions carry schema/catalogue/contract-version checks. This map is not a full per-tool input/output/error catalog. |
| Audio and Ellai sessions | `src/SwiftAid.Contracts/Ellai/EllaiSessionContracts.cs`; `src/SwiftAid.Contracts/Ellai/EllaiSegmentedAudio.cs`; `src/SwiftAid.Gateway/Ellai/`; `src/SwiftAid.Application/Ellai/` | Session request/view and audio-segment contracts are distinct from managed case commands. Segmented voice packets use `swiftaid.voice.segmented.v1` with 32-byte little-endian ESV1 headers and 24 kHz sample positions; gateway validates and streams them to the transcription path. Packet and C#/browser capture-gate position limits are capped at 10 minutes and reject overflow before state advancement. Transport, persistence, prepared audio and broader client recovery contracts need a complete field/version crosswalk. |
| UI registry | `src/SwiftAid.Application/Ellai/UiIntelligence/EllaiUiRegistry.cs`; `src/SwiftAid.Contracts/Ellai/` | Registry snapshots include catalogue and UI contract versions; admission validates against the observed snapshot. |
| Public projections | `src/SwiftAid.Contracts/Sessions/CommunitySessionContracts.cs`; `src/SwiftAid.Contracts/Notifications/NotificationItemView.cs`; `src/SwiftAid.Contracts/Surveillance/`; Gateway public/session endpoints | Separate public/session and minimized view contracts exist. This inventory does not demonstrate a complete projection schema catalogue or verify privacy across all serializers/routes. |

## Verified case-command/event vertical crosswalk — 2 November 2026

| Boundary | Contract/version | Producer and consumer path | Persistence/readback | Evidence and limit |
|---|---|---|---|---|
| Staff case command | `CaseCommandInput.SchemaVersion`; Gateway supports wire schema 1 at `POST /api/v1/cases/{caseId}/commands` | Gateway validates/maps the wire request; `CaseCommandProcessor` applies authorization and expected-version policy; `CanonicalCaseCommandHasher` binds idempotency to the accepted command | PostgreSQL command persistence records the event, receipt, audit/outbox state and relevant projection in the existing transaction | Endpoint mapping and command authorization/hash tests pass. Not a complete inventory of every command or consumer. |
| Follow-up acknowledgement | `AcknowledgeFollowUpCommand` (`CommandEnvelope.cs`); command ID, expected aggregate version, follow-up ID and server-recorded time | Shared work-management client → Gateway case-command endpoint → application command processor → domain aggregate. Only the assigned actor with `ManageFollowUp` can acknowledge an open scheduled follow-up. | `CaseFollowUpAcknowledged` is serialized, strictly replayed and projected as `Acknowledged`; the item remains in authorized assigned/open due queues. | Domain/application/client/UI tests pass; strict journal-reader suite 34/34; previous disposable PostgreSQL persistence/queue test 1/1; synthetic Chromium passes at 390/1280px. No physical-device or deployed-provider claim. |
| Case event journal | `CaseEventEnvelope` schema-v1 vocabulary; `CaseEventSerializer` is the event producer; `CaseEventJournalReader` enforces event-specific fields before replay | Application emits typed domain events; serializer writes schema/type/payload; strict reader validates and reconstructs the event for replay | PostgreSQL stores event metadata and protected payload; projection updates are applied within command persistence | Post-commit journal-reader regression 34/34. Contract inventory does not yet reconcile every enum member, serializer arm, DB constraint, fixture and consumer against the complete implementation event catalogue. |

During this crosswalk review, one duplicate `CaseFollowUpAcknowledged` replay arm was found and removed in commit `af2a6ed`; the post-commit `CaseEventJournalReaderTests` suite passes 34/34 and source inspection confirms one replay arm. Assistance event enum formatting was normalized in the same fix.

## Schema-to-event reconciliation - 3 November 2026

The contract enum, serializer, strict journal reader and `contracts/events/case-event.v1.schema.json` were compared by event name. The v1 schema initially omitted 10 of 24 `CaseEventType` values even though the serializer emitted them and the reader accepted them: `CaseAssignmentAcknowledged`, `CaseCollaboratorGranted`, `CaseCollaboratorRevoked`, and the seven assistance event types. The schema now includes every enum value, payload definition and event-to-payload mapping. The serializer test suite checks that the schema enum matches the contract enum, all enum values have a conditional payload mapping, all `oneOf` references resolve, and added event families serialize with schema version 1.

- PASS - `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~CaseEventSerializationTests --logger 'console;verbosity=minimal'` - 37 passed, 0 failed, 0 skipped.
- PASS - full Application.Tests - 909 passed, 3 skipped, 0 failed; skipped cases are live provider probes.
- PASS - `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~CaseEventJournalReaderTests --logger 'console;verbosity=minimal'` - 35 passed, 0 failed, 0 skipped. The suite also compares the EF design-time `ck_case_events_type` check constraint vocabulary with the schema.
- FAIL then PASS - the first integration test invocation attempted a WebAssembly native link and failed because `emcc` was not on PATH; the scoped rerun with `WasmBuildNative=false` passed.

This closes the schema vocabulary/mapping and EF-model constraint vocabulary gaps. It does not establish the applied database constraint or persistence/readback for every event, all producer/consumer coverage, or compatibility for every public contract; SW-01-004 remains partial.

## Forward migration for the case-event type constraint - 3 November 2026

Read-only inspection of the active local `swiftaid_dev` PostgreSQL 17 database found `ck_case_events_type` allowed only the original 13 event names, while the current contract, schema and EF model expose 24. This is a concrete runtime persistence incompatibility for the 11 later event types. No live database was changed. Added migration `20261103153000_CaseEventTypeConstraint` to replace the stale allowlist with all 24 canonical `CaseEventType` values. Its Down path restores the 13-name prior constraint; PostgreSQL will reject that rollback if any newly supported event rows exist, preserving data and the active constraint.

Added opt-in `CaseEventTypeConstraintPostgresTests` which applies the complete migration chain on a disposable PostgreSQL 17/PostGIS 3.6 instance, confirms all enum names appear in the applied constraint, and confirms an unknown event insert fails specifically with `ck_case_events_type`.

- PASS - `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~CaseEventTypeConstraintPostgresTests --logger 'console;verbosity=minimal'` with `SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES=1` and a task-owned disposable loopback database - 1 passed, 0 skipped.
- PASS - full migration chain applied from empty database through `20261103153000_CaseEventTypeConstraint`; test verified all 24 values and unknown-type rejection.
- NOT VERIFIED - migration has not been applied to the shared local `swiftaid_dev` database; runtime writes using later event types may still fail there until an authorized deployment/migration.

The v1 schema, EF model, forward migration, and fresh applied PostgreSQL constraint now share the 24-value event vocabulary. End-to-end persistence for every event family and the remaining SW-01-004 surfaces remain open.

## Worker job envelope cross-language alignment - 3 November 2026

The C# publisher contract and Python envelope validator both use schema v1, the same eight exact camelCase fields, the same seven job-type names, the 64 KiB envelope bound, and UTC-only creation timestamps. Review found the Python validator accepted organization IDs with leading/trailing whitespace, more than 200 UTF-16 code units, and control characters that `JobEnvelope.Validate()` rejects. The Python validator now applies equivalent rules, including .NET-compatible UTF-16 length counting. This is envelope validation only; `workers/src/swiftaid_workers/jobs/consumer.py` does not execute, claim or ACK work.

- PASS - `uv run --project workers python -m pytest workers/tests/integration/test_job_delivery.py -q` - 51 passed.
- PASS - `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~JobEnvelopeTests --logger 'console;verbosity=minimal'` - 12 passed.
- PASS - `git diff --check`.

Worker execution/result schema, producer-to-broker runtime, ACK/retry lifecycle and all other SW-01-004 surfaces remain open.

## Worker job subject and execution registry - 3 November 2026

`JobType` currently has seven values. The .NET `JetStreamPublisher.GetSubject` switch and Python `JOB_SUBJECT_SUFFIXES` agree on these canonical subjects. The implementation plan in `implementation.md` §10.1 names durable consumers for Report, CaseSummary, OpsContinuity, Surveillance, Knowledge and Reminder; §10.1a separately adds MemoryIndex. This is a registry/intent crosswalk, not evidence those consumers are running.

| Job type | Subject / intended durable | .NET publisher behavior | Worker and authoritative state found in source |
|---|---|---|---|
| Report | `swiftaid.jobs.report` / `report-worker-v1` | Subject mapping exists | No general job consumer or result store wired; `workers/src/swiftaid_workers/jobs/consumer.py` only validates the envelope. |
| CaseSummary | `swiftaid.jobs.case-summary` / `summary-worker-v1` | Subject mapping exists | No general job consumer or result store wired; only envelope validation exists. |
| OpsContinuity | `swiftaid.jobs.ops-continuity` / `ops-continuity-worker-v1` | Subject mapping exists | No general job consumer or result store wired; only envelope validation exists. |
| Surveillance | `swiftaid.jobs.surveillance` / `surveillance-worker-v1` (approval-gated in plan) | Explicitly rejected as disabled before publish | Persistence capability gate and result/dead-letter paths also reject this type; no consumer found. |
| Knowledge | `swiftaid.jobs.knowledge` / `knowledge-worker-v1` | Subject mapping exists | No general job consumer or result store wired; only envelope validation exists. |
| Reminder | `swiftaid.jobs.reminder` / `reminder-worker-v1` | Subject mapping exists | No general job consumer or result store wired; only envelope validation exists. |
| MemoryIndex | `swiftaid.jobs.memory-index` / `memory-index-v1` (§10.1a) | Subject mapping exists | No general job consumer or result store wired; only envelope validation exists. |

Publisher tests establish explicit subject selection, expected stream and message ID, minimized receipt handling and disabled-type behavior; they use a fake transport and do not prove a live JetStream job consumer. PASS `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~JobPublisherContractTests --logger 'console;verbosity=minimal'` - 7 passed. Envelope validation remains PASS C# 12/12 and Python 51/51 as recorded above.

The repository contains `JobEntity`, `IntegrationOutboxEntity`, `WorkerInboxEntity`, result/dead-letter entities and transition logic under `src/SwiftAid.Infrastructure/Persistence/Jobs/`, but `SwiftAidDbContext` has no corresponding `DbSet`/mapping and the migration chain has no job-table migrations. Therefore those types and state transitions are not evidence of durable PostgreSQL job state. The `classification/consumer.py` path is a separate ingestion-notice worker and does not consume `SWIFTAID_JOBS`.

**Gap:** the implementation plan requires gateway-owned durable job state, claim/lease/fencing, worker-scoped reads, result schema validation, commit-before-ACK, retry/DLQ and recovery. The generic job path currently has a versioned envelope, publisher mapping and source-level state-machine components, but no connected durable storage or general consumer execution. This gap requires implementation work under the worker/job tasks; it is not closed by envelope parity.

## Segmented audio frame bounds - 3 November 2026

`EllaiSpeechEvent.Validate` previously checked `SamplePosition + pcmSamples > MaximumPosition`. With unchecked `long` arithmetic, a malicious `long.MaxValue` position plus an audio sample could wrap negative and pass validation. The check now compares `SamplePosition > MaximumPosition - pcmSamples`, rejecting overflow before encoding or gateway decoding.

- PASS - focused `EllaiSpeechEventContractTests` 4/4, including last-valid-sample, position-at-limit, `long.MaxValue` encode and malformed decode.
- PASS - `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~EllaiSilenceGateTests --logger 'console;verbosity=minimal'` - 4 passed.
- PASS - `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~EllaiSegmentedAudioTests --logger 'console;verbosity=minimal'` - 6 passed.

This verifies packet validation and local consumer regressions, not microphone/device, WebSocket provider, or deployed voice behavior.

The shared C# and browser JavaScript capture gates reject a frame that would exceed the ESV1 ten-minute sample-position limit before advancing their positions. Each accepts the last valid frame and a heartbeat at the limit; the next frame fails without mutating position. JavaScript also rejects invalid initial stream identities/positions. EV-072 and EV-073 record the focused producer-boundary evidence. Client recovery/UX after the limit and live capture are not covered.

## Ellai read-tool registry and dispatch - 3 November 2026

`EllaiToolNames.All` is the canonical advertised allowlist (18 names). `TryResolveCanonical` accepts two compatibility aliases (`ellai_voice_capability` and `ellai_guidance_session`) and resolves them to canonical handlers; aliases are not advertised. `InvokeAsync` applies session/profile allowlisting, a remaining-call budget, required scope, an eight-argument cap, bounded argument checks, reasoning-text consent (with the documented public-product-help exception), policy authorization, and the common result-byte bound before dispatch. Results share `EllaiToolResult` fields: tool name, allowed/denial reason, evidence, summary, item count, truncation and reason code. This catalog is source-derived and does not claim provider-schema registration or live model execution.

| Canonical tool(s) | Arguments advertised to model | Dispatch / data boundary | Version or persistence evidence |
|---|---|---|---|
| `ellai_state` | projection, limit, optional country/workflow/sort | `ReadStateAsync`; feature or authorized owned-case queue | Reads current application projection; no independent wire schema identified in broker. |
| `ellai_evidence` | ref, optional field | Broker returns bounded versioned evidence references | Evidence references carry provenance/version metadata; backing source varies by reference. |
| `ellai_topic` | ref | `ReadTopicsAsync`; session topic/checkpoint service | Durable topic/checkpoint entities exist; see topic service and persistence entities. |
| `ellai_capabilities` | optional feature | Static/current capability descriptors | No separate serialized schema identified. |
| `ellai_receipts` | optional receipt | `ReadReceiptsAsync`; approval receipt store, session-bound | Durable approval store; result is minimized evidence. |
| `ellai_productfacts` | optional topic | `ReadProductFacts`; versioned nonmedical product-fact catalog | Catalog supplies versioned facts; no external model call. |
| `ellai_now` | none | Server UTC clock | Result uses round-trip UTC and Unix milliseconds; ephemeral. |
| `ellai_docs_search` | q, optional limit | Approved technical documentation index | Read-only curated reference; no raw repository source. |
| `ellai_docs_get` | exact section | Approved section retrieval | Section identifier and provenance returned; read-only. |
| `ellai_ui_catalogue` | optional feature/goal/limit | `ReadUiCatalogueAsync`; entitlement-filtered UI registry | Registry/catalogue and UI contract versions are checked by admission. |
| `ellai_ui_observe` | controls, optional view/context/feature | `ReadUiObserveAsync`; client-posted mounted-control observation | Client observation is server-stamped for freshness; not a durable UI schema. |
| `ellai_ingestion_schema` | none | Canonical CSV capture schema and authority guidance | Static contract description; performs no submission. |
| `ellai_validate_observations` | observations_json | Bounded original-string validation; no inference or submission | JSON-array parser and CSV field bound; no persistence. |
| `ellai_locations` | q, optional country/limit | `ReadLocationsAsync`; public geographic registry | Read-only registry lookup; user selects a match. |
| `ellai_import_destinations` | none | `ReadImportsAsync`; current actor-authorized destinations | Reads import workspace authorization; no case contents. |
| `ellai_imports` | workspace_id, organization_id, country | `ReadImportsAsync`; caller's recent imports in one permitted destination | Import store read; returns minimized state. |
| `ellai_import_status` | import_id | `ReadImportsAsync`; caller's durable import status/counts | Durable import store read; does not submit or return case contents. |
| `ellai_classification_evidence` | case_id | `ReadClassificationAsync`; authorized persisted evidence for current case revision | Read-only classification query; no inference, diagnosis, or clinical probability. |

Source anchors: `src/SwiftAid.Application/Ellai/Tools/EllaiToolBroker.cs` (`EllaiToolNames`, `TryResolveCanonical`, `ToolDescriptions`, `InvokeAsync`); `src/SwiftAid.Application/Ellai/UiIntelligence/EllaiUiRegistry.cs`; `src/SwiftAid.Infrastructure/Persistence/Ellai/` (durable session/topic/receipt stores). The catalog exposes a concrete crosswalk for the broker surface, while remaining gaps include formal per-tool argument/result schemas, exhaustive test mapping, and source-to-consumer/persistence linkage for every evidence reference. These gaps keep SW-01-004 partial.

## Public and recipient-scoped projections - 3 November 2026

| Projection | Route / producer | Contract and privacy boundary | Evidence |
|---|---|---|---|
| Public intake | Anonymous `/public/v1/intake/*` in `PublicIntakeEndpoints` | `IntakeSubmission` is unverified reporter input without actor/organization authority; request JSON rejects unmapped fields and is capped at 64 KiB. Session capability and receipt proof travel in bounded headers, submissions/receipts use `Cache-Control: no-store`, and `IntakeReceipt` exposes only receipt/submission IDs and unverified routing/status fields, not case ID, assignee or organization. Versioned public metadata/country catalogues and bounded geography search/children are separate public projections. | `GuestIntakeTests` covers proof and header checks, unknown privileged field rejection, success receipt/no-store, malformed/unknown proof and metadata behavior. Receipt JSON test now asserts case/staff/organization fields are absent. `GeoRegistryEndpointTests` covers bounded search, invalid filters, missing registry and child lookup. |
| Public community session | Anonymous `GET /public/v1/sessions` and `/{sessionId}` in `PublicSessionEndpoints` | `PublicSessionView`; separate from staff `CommunitySessionView`. It excludes creator/reviewer actor IDs, self-review acknowledgement, attendee data and staff audit fields. Calendar and invitation routes render only service-approved public sessions. | `CommunitySessionEndpointTests` covers anonymous list/detail/calendar/invitation, unknown-session 404 and disabled-pipeline behavior. The serialized detail test now explicitly asserts staff-only and attendee fields are absent. |
| Notification feed | Authenticated `GET /api/v1/notifications` in `NotificationsController` | Recipient-authorized `NotificationFeedPage` containing `NotificationItemView`; display text is derived from a closed notification kind. It carries no subject, condition, location or media details; opaque resource reference remains a minimized navigation reference. This is private staff API data, not an anonymous public projection. | Source inspection confirms staff policy, current actor extraction, bounded page size and the minimized DTO; route-level runtime serialization was not exercised in this slice. |
| Surveillance area aggregate | Authenticated `GET /api/v1/surveillance/area-aggregates` in `MapAggregateEndpoints` | `SurveillanceAggregateResult` / `SurveillanceAggregateArea`; route requires staff authorization. Area rows contain aggregate counts only, no case/observation identifiers or coordinates. Suppressed rows must have null counts and denominator plus a reason; caller-readable cases only. | `MapAggregateEndpointTests` covers authorization, anonymous rejection, disabled service, filters and response; serialized test now asserts case/observation identifiers and coordinates are absent. Application/PostgreSQL aggregate query evidence remains in its focused suites. |

PASS - `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter "FullyQualifiedName~CommunitySessionEndpointTests|FullyQualifiedName~MapAggregateEndpointTests" --logger 'console;verbosity=minimal'` - 46 passed, 0 skipped. This covers these session and aggregate endpoint scenarios, not every public Gateway projection. Additional endpoint DTOs and full notification wire/privacy behavior remain to be catalogued or verified.

PASS - `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter "FullyQualifiedName~GuestIntakeTests|FullyQualifiedName~GeoRegistryEndpointTests" --logger 'console;verbosity=minimal'` - 22 passed, 0 skipped. This proves the focused anonymous intake and geo endpoints under synthetic TestServer services; it is not a deployed Gateway or real public-data acceptance run.

## Gaps and follow-up

- Build a generated or maintained crosswalk of every command/event/job/tool/audio/UI/public-projection type to schema version, authoritative owner, producer, consumer, persistence form, compatibility policy and relevant tests.
- Reconcile event enum members and serializer registrations against the versioned event list required by `implementation.md` section 8; source mapping alone does not prove the required event families are fully implemented.
- Document the worker job type registry and distinguish envelope validation from execution, durable acknowledgement and retry behavior.
- Trace each audio/session version from client capture through Gateway, storage, response preparation and cancellation/error paths.
- Complete the remaining endpoint projection inventory; add focused serialization/privacy assertions where route tests do not currently prove the DTO boundary.
- Add formal per-tool argument/result schemas and map each tool to focused authorization, denial, truncation, and persistence tests; the source registry/dispatch inventory above is not a substitute for those checks.

The v1 event schema was changed in this reconciliation; no migration or package change was needed. Current ownership for broader shared contract/schema work remains to be confirmed from active handoffs (BL-005).
