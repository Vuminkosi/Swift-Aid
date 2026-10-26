# Task ownership snapshot

Source contract: [implementation.md](../implementation.md) v2.4; product criteria: [spec.md](../spec.md) v1.4. Snapshot: 27 October 2026. The master orchestrator retains global task, contract, and integration authority. Assignment rows below are a status snapshot, not exclusive path reservations; implementation lane must maintain active path ownership in its handoffs. No ownership is inferred for future work.

P0.1, P0.3, P1.1, and P1.2/P1.2A have observed implementation assignments or results in the current snapshot. Other source tasks have no observed implementation owner recorded here. This table is not an exclusive path reservation; update exact paths from current lane handoffs. The documentation lane owns docs/ records for this task; it does not own code or the source documents.

| Task | Source task name | Observed status / owner |
|---|---|---|
| P0.1 | Create the fresh repository and locked build baseline | PARTIAL / IN PROGRESS — locked restore, integrated solution build, and Domain/Application test snapshot PASS. Android MudBlazor host wiring and zoom source review PASS; P0.1 acceptance and AT-36 remain NOT VERIFIED. |
| P0.2 | Prove native MAUI + Datasync on Android before UI investment | PARTIAL / IN PROGRESS — Android Release build PASS; AVD boot attempt failed before APK install. Device install/render/restart/offline/Datasync acceptance NOT VERIFIED. |
| P0.3 | Verify Meta access and define capability/budget controls | IMPLEMENTER ASSIGNMENT COMPLETE, partial guard slice — `/root/implementation_orchestrator_swiftaid_foundation/backend_implementer_swiftaid_meta_budget_guards`; focused tests 15/15 and Application.Tests 23/23 reported PASS after a test-only fix. Production provider route/call/store/DI and real Meta capability remain NOT VERIFIED. |
| P1.1 | Define schemas, aggregate invariants, and event replay | IMPLEMENTATION ASSIGNMENT COMPLETE — implementer `/root/implementation_orchestrator_swiftaid_foundation/backend_implementer_swiftaid_case_aggregate`; independent API-contract/code reviews PASS; serializer extension 12/12; full-solution test snapshot passed Domain 15 + Application 35; FormatChecker validated all eight fixtures. Broader P1.1/AT acceptance remains NOT VERIFIED. |
| P1.2 | Implement first-party identity and resource policies | PARTIAL / IN REVIEW — P1.2A findings and the wrong-CaseId-result regression were addressed. A separate grouped-grant authorization/manifest sub-slice now has 22 focused authorization tests PASS and independent code/API/data-integrity reviews PASS; latest follow-ups resolved expiry-recheck and canonical-sensitivity-fixture findings; dedicated security R3 review PASS with no findings in the inspected scope. P12B is integrated at `060155d00962d8ee513103df5b74be755a1d0ab8`; 22/22 focused authorization tests PASS before and after commit; the post-commit focused run at the recorded commit passed with .NET SDK 10.0.403. The full application suite and P1.2 acceptance remain NOT VERIFIED. Production auth/grant store/DI/endpoint enforcement, persistence, routes, migration and sync-reader compatibility remain NOT VERIFIED; P1.2 and runtime AT-02/09/10 acceptance are not complete. |
| P1.3 | Implement Facebook, Google and organizational SSO | NOT STARTED — no implementation evidence recorded |
| P2.1 | Implement atomic command processing, receipts, and persistence | PARTIAL only: a P2.1A schema/migration slice is present in six untracked persistence files with migration/data-integrity reviews and bounded disposable PostGIS fixture evidence reported PASS; no P2.1A commit ID and it is NOT YET INTEGRATED. This partial slice does not establish full P2.1 processing, production persistence, routes/DI, payload protection, bootstrap, retention, or runtime acceptance. |
| P2.2 | Implement human and animal report registration/correction | NOT STARTED — no implementation evidence recorded |
| P2.3 | Expose authorized read-only Datasync projections | NOT STARTED — no implementation evidence recorded |
| P2.4 | Implement private global guest intake and routing | NOT STARTED — no implementation evidence recorded |
| P3.1 | Persist drafts and local-only state | NOT STARTED — no implementation evidence recorded |
| P3.2 | Implement domain outbox and reconnect orchestration | NOT STARTED — no implementation evidence recorded |
| P3.3 | Handle scope changes, offline grants, and safe cache refresh | NOT STARTED — no implementation evidence recorded |
| P3.4 | Implement guest offline capture and receipt continuity | NOT STARTED — no implementation evidence recorded |
| P4.1 | Record, retain, and play original voice offline | NOT STARTED — no implementation evidence recorded |
| P4.2 | Authorize and resume original-media uploads | NOT STARTED — no implementation evidence recorded |
| P4.3 | Connect transcript revisions and voice-first comments | NOT STARTED — no implementation evidence recorded |
| P5.1 | Implement ownership, collaboration, and assistance delivery | NOT STARTED — no implementation evidence recorded |
| P5.2 | Implement follow-ups, observations, and escalation | NOT STARTED — no implementation evidence recorded |
| P5.3 | Implement resolution, reopening, and complete timelines | NOT STARTED — no implementation evidence recorded |
| P6.1 | Publish transactional jobs reliably | NOT STARTED — no implementation evidence recorded |
| P6.2 | Implement the Python worker control protocol | NOT STARTED — no implementation evidence recorded |
| P6.3 | Dispatch durable due work and reminders | NOT STARTED — no implementation evidence recorded |
| P7.1 | Seed all requested profiles and reproducible scenarios | NOT STARTED — no implementation evidence recorded |
| P7.2 | Ingest identified official sources with provenance and rights gates | NOT STARTED — no implementation evidence recorded |
| P7.3 | Implement source-filtered retrieval and offline education packs | NOT STARTED — no implementation evidence recorded |
| P7.4 | Implement Qdrant memory and cache index infrastructure | NOT STARTED — no implementation evidence recorded |
| P8.1 | Build the .NET Meta adapter and authorized MCP tool layer | NOT STARTED — no implementation evidence recorded |
| P8.2 | Implement transcription, structured extraction, and confirmation | NOT STARTED — no implementation evidence recorded |
| P8.3 | Implement factual continuity and approved administrative execution | NOT STARTED — no implementation evidence recorded |
| P8.4 | Evaluate AI behavior and enforce cost limits | NOT STARTED — no implementation evidence recorded |
| P8.5 | Integrate authorized AI recall and context-safe cache reuse | NOT STARTED — no implementation evidence recorded |
| P8.6 | Implement durable multi-turn reasoning and clarification | NOT STARTED — no implementation owner or implementation result observed. P0 bounded scope; depends on P8.3, P8.5, P6.2, and P1.2. AT-73–AT-76, SA-LOOP-01–15, focused/integration/Python tests, and real supported Meta multi-turn smoke remain NOT VERIFIED. |
| P9.1 | Implement deterministic pattern detection | NOT STARTED — no implementation evidence recorded |
| P9.2 | Generate restricted investigation briefs and human decisions | NOT STARTED — no implementation evidence recorded |
| P9.3 | Test corrections, false signals, and cross-sector negative controls | NOT STARTED — no implementation evidence recorded |
| P10.1 | Build session scheduling, public views, and calendar export | NOT STARTED — no implementation evidence recorded |
| P10.2 | Implement manual sharing and gated Facebook/WhatsApp adapters | NOT STARTED — no implementation evidence recorded |
| P10.3 | Implement authenticated event subscriptions and account preferences | NOT STARTED — no implementation evidence recorded |
| P11.1 | Complete the low-input mobile case journey | NOT STARTED — no implementation evidence recorded |
| P11.2 | Complete officer/public surfaces without scope leakage | NOT STARTED — no implementation evidence recorded |
| P12.1 | Run cross-stack privacy and failure-injection tests | NOT STARTED — no implementation evidence recorded |
| P12.2 | Package a reproducible hosted demo and installable APK | NOT STARTED — no implementation evidence recorded |
| P12.3 | Freeze scope and prepare honest demonstration evidence | NOT STARTED — no implementation evidence recorded |

## Recent implementation sub-slice evidence

The following completed assignments are implementation/test evidence only and do not change source task ownership or P0/P1 acceptance status:

- `/root/implementation_orchestrator_swiftaid_foundation/backend_implementer_swiftaid_provider_adapters` completed the provider-configuration test-only regression. `ProviderConfigurationTests` reported 8/8 PASS for synthetic configuration checks and FCM token serialization. It did not call providers or verify service readiness.
- `/root/implementation_orchestrator_swiftaid_foundation/frontend_implementer_swiftaid_loader_prep` completed the shared Gateway/Mobile loader implementation. Its handoff reports Gateway and Android Release builds with 0 warnings/errors and JavaScript syntax checks. The initial browser attempt did not observe a loader visual and is historical; corrected loader and drawer browser scripts were independently reviewed and final bounded reruns passed (hashes and exact scope are in `UI-COMPONENTS.md`). Android device rendering remains NOT VERIFIED.
- `/root/implementation_orchestrator_swiftaid_foundation/backend_implementer_swiftaid_provider_adapters` later completed the TLS guard slice. The independent provider configuration suite reported 19/19 PASS; this supersedes its earlier 8/8 test-only result. Review/test scope is configuration only; no live provider call or readiness is established. Master accepted the exact-host Development plaintext exception for private Docker hosts only; Production TLS remains required.
- `/root/implementation_orchestrator_swiftaid_foundation/devops_engineer_swiftaid_foundation` completed the checksum-pinned local MinIO/mc source-build and bounded isolated MinIO/Redis/NATS checks. Images are local-only; the full Compose stack was not started, and app adapters through DI remain NOT VERIFIED.
- `/root/implementation_orchestrator_swiftaid_foundation/frontend_implementer_swiftaid_loader_prep` implemented the shared UI foundation in `src/SwiftAid.UI`, referenced by Gateway and Mobile. It provides common theme/provider, loader, heading, body-text, and service-boundary components. Independent code and UX reviews plus Gateway/Android Release builds are reported PASS; exact Release build command strings were not recorded. Independent accessibility source review closed Home dark-secondary and toggle-state S2s at source. The earlier listener 5187 no-result observation is historical. A later bounded Gateway browser check passed mouse-driven `aria-expanded` matching drawer state, Space opening and Enter closing, and `aria-controls` resolving the drawer. The TestHost build and both browser script syntax checks passed; no page errors, HTTP failures or external requests occurred. Browser version, screen-reader behavior, rendered contrast and Android/device acceptance remain NOT VERIFIED. `/root/implementation_orchestrator_swiftaid_foundation/devops_engineer_swiftaid_foundation` reported locked restores for solution and Mobile. Full migration of host-specific pages/layout is not claimed.
- The current working tree contains provider options and MinIO, Redis, JetStream, and FCM source adapters. The original adapter implementation owner was not supplied to this documentation lane, so no source-task ownership is inferred. PostgreSQL persistence, Qdrant adapter, live provider integration, and provider readiness remain NOT VERIFIED.

### P1.2 grouped-grant authorization/manifest sub-slice (partial)

The current slice correlates each immutable grant group across organization, workspace, country, region, sector, sensitivity, and operation; preserves opaque case-sensitive identifiers; retains group-local expiry, actor epoch, and status; rechecks expiry after descriptor I/O; and preserves group boundaries in manifests. Parent-reported focused authorization tests: 22 PASS. Independent code, API-contract, and data-integrity reviews: PASS; latest follow-ups resolved expiry-recheck and canonical-sensitivity-fixture findings; dedicated security R3 review PASS with no findings in the inspected scope. P12B is integrated at commit `060155d00962d8ee513103df5b74be755a1d0ab8`; the seven reviewed paths/blobs match and the index was empty. Focused authorization tests passed 22/22 before and after commit; the post-commit filtered run used .NET SDK 10.0.403. The full Application.Tests suite remains NOT VERIFIED. This slice contains authorization logic and manifest projection only. No SQL/grant-store implementation, persistence, routes, sync consumer/client reader, or migration is included. It does not complete P1.2 or establish production authorization or compatibility beyond the inspected checkout.

Current reviewed source hashes supplied by the parent: `src/SwiftAid.Application/Authorization/CaseAccessPolicy.cs` `A4B37FAC753A5F09A6AA5A014F950D7E21F5212F9CED1A004603D9AF7813D6F8`; `src/SwiftAid.Application/Authorization/IScopeService.cs` `7F0B100513117749E1C6E3DB0500E7140CA3B6434EBB3C538D1CEB6D842C8BCD`; `src/SwiftAid.Contracts/Sync/ScopeGrantManifest.cs` `999FF2CAF6DE0B2A9DC9DCD272A0BC393312DBDBD9E4C2E20549360EB167736D`; `tests/SwiftAid.Application.Tests/AuthorizationPolicyTests.cs` `C2D8D68AE9B7E8E65E2FE279BBC5C1E78D67373DC23344E9AD1720C085B41C85`.

### P2.1A persistence schema/migration sub-slice (partial)

This is only the P2.1A persistence/schema sub-slice, not full P2.1. Six source files were reported untracked at canonical HEAD `7c55fa914c518be7db4d42755d07adda0712dd78`; no P2.1A commit ID was reported, so this slice is NOT YET INTEGRATED. Migration reviewer and data-integrity review PASS for the exact six-file schema. The additive `Up` adds new tables without backfill; `Down` drops those new tables child-first. Version concurrency tokens and positive-version constraints are present. Full P2.1 persistence/runtime acceptance remains NOT VERIFIED.

Owner-reported disposable PostGIS 17.11 checks included Up / Down-to-zero / Up, FK/unique/epoch/hash/shareVersion constraints, sequential stale revoke-vs-renew affected-row results 1 then 0, and manual role-negative checks; the exact owner command was not recorded. An independent disposable PostgreSQL/PostGIS recheck used psql 17.11 and `docker exec <task-owned-container> psql -X -v ON_ERROR_STOP=1 -U p21_migration_owner -d swiftaid_p21a -c <SQL>`: one inline `BEGIN`/`ROLLBACK` synthetic transaction accepted a valid insert, rejected version 0, accepted CAS version 1 (one row), rejected stale version 1 (zero rows), denied the runtime role update, and left zero rows after rollback. This verifies sequential stale-predicate behavior, not a concurrent race. No reusable script or image digest was recorded.

The six reported final source hashes are listed in `PROGRESS.md`. No production writer monotonic CAS, runtime role bootstrap, SQL/grant/descriptor stores, processor, routes/DI, consumer, payload protection, bootstrap/retention, or production database enforcement is verified.


## Shared-resource coordination

- Root solution, SDK pin, package manifests/locks, contracts, migrations, source catalogues, fixtures, generated outputs, and service environments require a designated owner and serialized edits as specified by implementation.md.
- Canonical Git index and commits are reserved for the authorized code-committer-merger after review and required checks. This documentation task performed no Git operation.
- The three design PNGs and swiftaid-icon.png were present as untracked inputs before implementation was observed. Preserve them as user-provided reference assets; their original creator/license is unresolved.
