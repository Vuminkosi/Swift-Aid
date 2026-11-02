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

## Gaps and follow-up

- Build a generated or maintained crosswalk of every command/event/job/tool/audio/UI/public-projection type to schema version, authoritative owner, producer, consumer, persistence form, compatibility policy and relevant tests.
- Reconcile event enum members and serializer registrations against the versioned event list required by `implementation.md` section 8; source mapping alone does not prove the required event families are fully implemented.
- Document the worker job type registry and distinguish envelope validation from execution, durable acknowledgement and retry behavior.
- Trace each audio/session version from client capture through Gateway, storage, response preparation and cancellation/error paths.
- Verify projection contracts and serialization/privacy boundaries against their routes and consumers.

No contract, migration, package or generated file was changed. Current ownership for shared contract/schema edits remains to be confirmed from active handoffs (BL-005); no migration is proposed by this inventory.
