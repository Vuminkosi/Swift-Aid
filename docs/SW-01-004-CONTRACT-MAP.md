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
| Worker jobs | `src/SwiftAid.Contracts/Jobs/JobEnvelope.cs`; `workers/src/swiftaid_workers/jobs/consumer.py`; classification consumer under `workers/src/swiftaid_workers/classification/` | .NET envelope declares current schema version 1; Python validates `schemaVersion` as literal 1. Generic `jobs/consumer.py` validates an envelope only; the classification worker has its own implemented notice path. Do not infer a general job execution/ACK implementation from the validator. |
| AI tools and plans | `src/SwiftAid.Application/Ellai/Tools/`; `src/SwiftAid.Application/Ellai/UiIntelligence/`; `src/SwiftAid.Contracts/Ellai/` | Tool broker mediates constrained actions; guidance drafts and admissions carry schema/catalogue/contract-version checks. This map is not a full per-tool input/output/error catalog. |
| Audio and Ellai sessions | `src/SwiftAid.Contracts/Ellai/EllaiSessionContracts.cs`; `src/SwiftAid.Contracts/Ellai/EllaiSegmentedAudio.cs`; `src/SwiftAid.Gateway/Ellai/`; `src/SwiftAid.Application/Ellai/` | Session request/view and audio-segment contracts are distinct from managed case commands. Gateway includes a WebSocket voice endpoint and application session services. Transport, persistence, prepared audio and client capture contracts need a complete field/version crosswalk. |
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
- PASS - `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~CaseEventJournalReaderTests --logger 'console;verbosity=minimal'` - 34 passed, 0 failed, 0 skipped.
- FAIL then PASS - the first integration test invocation attempted a WebAssembly native link and failed because `emcc` was not on PATH; the scoped rerun with `WasmBuildNative=false` passed.

This closes the schema vocabulary/mapping gap only. It does not establish persistence/readback for every event, DB constraint alignment, all producer/consumer coverage, or compatibility for every public contract; SW-01-004 remains partial.

## Gaps and follow-up

- Build a generated or maintained crosswalk of every command/event/job/tool/audio/UI/public-projection type to schema version, authoritative owner, producer, consumer, persistence form, compatibility policy and relevant tests.
- Reconcile event enum members and serializer registrations against the versioned event list required by `implementation.md` section 8; source mapping alone does not prove the required event families are fully implemented.
- Document the worker job type registry and distinguish envelope validation from execution, durable acknowledgement and retry behavior.
- Trace each audio/session version from client capture through Gateway, storage, response preparation and cancellation/error paths.
- Verify projection contracts and serialization/privacy boundaries against their routes and consumers.

The v1 event schema was changed in this reconciliation; no migration or package change was needed. Current ownership for broader shared contract/schema work remains to be confirmed from active handoffs (BL-005).
