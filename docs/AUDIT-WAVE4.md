# Audit Wave 4 — Close-out (read-only)

Date (UTC): 2026-10-28. Scope: Gateway route inventory, latest-migration vs snapshot drift (static), disabled-pipeline DI contract.

## 1. Controllers under src/SwiftAid.Gateway vs endpoint-mapped paths

Controllers/ exists with 3 controller classes. No path duplicates vs endpoint-mapped routes.

| Controller (file:line) | Base route | Verbs / full routes |
|---|---|---|
| `SpikeTableController` ([Controllers/SpikeTableController.cs](src/SwiftAid.Gateway/Controllers/SpikeTableController.cs:15)) | `mobile/api/tables/spike` | Datasync `TableController<T>` conventions (query/create/read/replace/delete) under the base route; write ops return 405 (read-only spike, lines 98-134) |
| `NotificationsController` ([Controllers/Notifications/NotificationsController.cs](src/SwiftAid.Gateway/Controllers/Notifications/NotificationsController.cs:16)) | `api/v1/notifications` | `GET /` (line 33); `POST /{id:guid}/read` (line 76); `POST /{id:guid}/dismiss` (line 87); `GET /stream` SSE (line 98) |
| `PushDevicesController` ([Controllers/Notifications/PushDevicesController.cs](src/SwiftAid.Gateway/Controllers/Notifications/PushDevicesController.cs:17)) | `api/v1/push/devices` | `POST /` (line 28); `DELETE /{installationId:guid}` (line 89) |

Endpoint-mapped routes (all via `Map*` in Endpoints/, wired in [Program.cs](src/SwiftAid.Gateway/Program.cs:77-91)):

| Endpoint file | Routes |
|---|---|
| `CaseCommandEndpoints.cs:40` | `POST /api/v1/cases/{caseId:guid}/commands` |
| `CaseQueueEndpoints.cs:31` | `GET /api/v1/cases/mine` |
| `CaseTimelineEndpoints.cs:31` | `GET /api/v1/cases/{caseId:guid}/timeline` |
| `IdentityEndpoints.cs:34-55` | `/auth` group: `GET /providers`, `GET /facebook/web/options`, `POST /facebook/web/exchange`, plus federation-gated (`GET /antiforgery`, `POST /{providerId}/challenge`, `GET /external/callback`, `POST /reauthenticate/{providerId}`, `GET /reauthenticate/callback`, `POST /identities/{providerId}/link`, `GET /identities/link/callback`, `POST /identities/{providerId}/unlink`, `POST /logout`) and identity-gated (`GET\|POST /connect/authorize`, `POST /connect/token`, `GET /connect/logout`) |
| `IntakeCoordinationEndpoints.cs:39-60` | `GET /api/v1/intake/coordination/queue`, `GET /api/v1/intake/coordination/{id}`, `GET /api/v1/intake/coordination/queues`, `POST /api/v1/intake/coordination/{id}/actions` |
| `IntakeDecisionEndpoints.cs:27` | `POST /api/v1/intake/{id}/decisions` |
| `ManagedCaseRegistrationEndpoints.cs:33` | `POST /api/v1/cases` |
| `PublicIntakeEndpoints.cs:23-39` (`/public/v1/intake` group, all `AllowAnonymous`) | `POST /sessions`, `POST /reports`, `POST /receipt`, `GET /metadata`, `GET /metadata/countries` |
| `Program.cs:90` | `GET /health/live` |

Duplicate/overlap verdict: none. Nearest neighbours that do NOT collide: `POST /api/v1/cases` vs `POST /api/v1/cases/{caseId:guid}/commands` (different depth); `GET /api/v1/cases/mine` vs `GET /api/v1/cases/{caseId:guid}/timeline` (`mine` is a literal segment, `{caseId:guid}` constrained); controller `api/v1/notifications` and `api/v1/push/devices` share the `api/v1` prefix with endpoints but no full-path overlap; `mobile/api/tables/spike` is in its own namespace.

## 2. Latest migration Up() vs ModelSnapshot (static, no dotnet ef)

Latest migration by timestamp: `20261028020521_P24IntakeCoordinationQueue` ([Up()](src/SwiftAid.Infrastructure/Persistence/Migrations/20261028020521_P24IntakeCoordinationQueue.cs:12-133)). Snapshot: [SwiftAidDbContextModelSnapshot.cs](src/SwiftAid.Infrastructure/Persistence/Migrations/SwiftAidDbContextModelSnapshot.cs). No drift found.

| Up() operation (migration file:line) | Snapshot counterpart (snapshot:line) | Match |
|---|---|---|
| Drop + re-add `ck_grant_group_operations_value` with `CoordinateIntake` (14-16, 123-126) | line 663 check SQL includes `CoordinateIntake` | Yes |
| 9 × `AddColumn` on `swiftaid_intake.intake_reports` (18-79) | `clarification_reason` 1410, `clarification_requested_at_utc` 1414, `coordinator_actor_id` 1422, `coordinator_assigned_at_utc` 1426, `routing_area_id` 1455, `routing_catalogue_version` 1459, `routing_country_code` 1463, `routing_region_id` 1467, `subject_scope` 1485 | Yes, all 9 |
| `CreateTable intake_coordination_actions` + PK `pk_intake_coordination_actions` + 3 check constraints + FK `fk_intake_coordination_actions_report` (81-115) | PK 1252-1253; checks 1260-1264 (`ck_intake_coordination_action`, `ck_intake_coordination_actor`, `ck_intake_coordination_version` with matching SQL); FK 2514 | Yes |
| `CreateIndex ix_intake_reports_coordination_queue` on `(routing_status, received_at_utc, receipt_id)` (117-121) | line 1502 | Yes |
| `CreateIndex ix_intake_coordination_actions_receipt` on `(receipt_id, acted_at_utc)` (128-132) | lines 1255-1256 | Yes |

## 3. Disabled-pipeline DI contract ([ServiceCollectionExtensions.cs](src/SwiftAid.Infrastructure/Configuration/ServiceCollectionExtensions.cs))

Guard: `RegisterCaseCommandProcessing` (lines 283-386); early return when disabled at lines 317-320 (`if (!commandOptions.Enabled) { return; }`).

Outside the block (registered even when disabled, lines 310-315):

| Service |
|---|
| `CaseCommandProcessingServiceOptions` (+ `IOptions<>`) |
| `CaseCommandProcessingOptions` (+ `IOptions<>`) |
| `ProtectedPayloadOptions` (+ `IOptions<>`; read from config only when enabled, else `new ProtectedPayloadOptions()`, lines 306-308) |

Inside the block (registered only when enabled, lines 322-385):

| Service |
|---|
| `SwiftAidDbContext` (only when `!identityEnabled`, lines 347-354; otherwise registered by `RegisterIdentity`) |
| `PostgresCaseCommandUnitOfWork`, `ICaseCommandUnitOfWork`, `ICaseCommandLeaseAccessor` |
| `PostgresCaseRegistrationUnitOfWork`, `ICaseRegistrationUnitOfWork`, `ICaseRegistrationPersistence`, `ICaseRegistrationAuthorization` |
| `ICurrentAccessGrantStore`, `ICaseAccessDescriptorStore`, `ICaseAccessPolicy` |
| `CaseEventSerializer`, `ProtectedPayloadDataProtectionProvider`, `IDataProtectionProvider` replacement |
| `PostgresProtectedCaseEventPayloadStore`, `IProtectedCaseEventPayloadStore` |
| `ICaseCommandPersistence`, `ICaseCommandProcessor` |
| `ICaseTimelineStore`, `ICaseTimelineQuery`, `ICaseQueueStore`, `ICaseQueueQuery` |
| `CaseCommandProcessingStartupValidator` (hosted service) + key-ring validator/inspector |

Contract verdict: holds. Disabled pipeline registers options only; no DbContext, no Postgres stores, no processor, no data-protection replacement, no startup validator.
