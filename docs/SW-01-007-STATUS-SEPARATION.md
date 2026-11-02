# SW-01-007 status-separation audit - 2 November 2026

## Outcome

Source inventory is partial. The repository has distinct contracts and state stores for several transitions, but this audit did not establish an end-to-end status model and UI copy for all acceptance, projection, media, AI and external-delivery workflows. A status shown by one subsystem must not be generalized to another subsystem's receipt.

## Observed state boundaries

| State | Source examples | Meaning boundary |
|---|---|---|
| Local transport/outbox | `src/SwiftAid.Client/Cases/CaseCommandOutboxEnvelope.cs`; `OfflineCaseCommandClient.cs` | A queued frozen intent is pending delivery; it is not server acceptance. |
| Command acceptance | `src/SwiftAid.Contracts/Cases/CaseCommands.cs`; Gateway case command endpoints | A command receipt/version is server-side command processing evidence, separate from a local send attempt. |
| Projection freshness | `src/SwiftAid.Contracts/Sync/ScopeManifest.cs`; Datasync/client projection code | Scope and version metadata exist; per-screen stale/fresh user feedback was not exhaustively mapped. |
| Media upload | `src/SwiftAid.Application/Attachments/AttachmentContracts.cs` and attachment Gateway/client flows | Reservation, chunk append, completion and status retrieval are explicit phases; upload completion is not case-command or AI completion. |
| AI processing | Classification and Ellai state contracts/services under `src/SwiftAid.Application/Classification/` and `src/SwiftAid.Application/Ellai/` | Pending/completed/failed and review states are subsystem-specific; an AI result does not imply human approval or a case event. |
| External delivery | Notification delivery contracts/entities/workers under `src/SwiftAid.Application/Notifications/`, `src/SwiftAid.Infrastructure/Persistence/Notifications/` and `src/SwiftAid.Gateway/Notifications/` | Queued/provider-accepted delivery states are separate from command acceptance. Provider receipt and actual recipient delivery are not interchangeable. |

## Closure work

Map each UI and API status to its authoritative state transition, owner, timestamp/version, retry/reconciliation action, and accessible user wording. Verify that transport submission, server acceptance, projection freshness, media upload, AI processing/review and external delivery remain separately visible in real client workflows. Relevant runtime/browser/device journeys were not executed for this inventory.

No source or test was changed or run. This audit records source-level status boundaries only; full client journeys remain NOT VERIFIED.
