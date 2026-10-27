# Specification alignment and decision ledger

Snapshot date: 26 October 2026. This ledger records approved contracts and open source discrepancies; it is not proof that software behavior exists.

## Current authoritative pair

- Product specification: [spec.md](../spec.md), v1.4, SHA-256 `8900B57EA08C9DB0704D26B19AB6289D4E4D7891D2609CC253CAF1D3A66B4AEE`.
- Implementation plan: [implementation.md](../implementation.md), v2.4, SHA-256 `09F9EB36683D8860D2904CB1604C6A5C65E7EC9C51A9380E3BFEF46A4A1F2023`.
- Current extracted inventories: 365 unique `SA-*` requirements, 76 `AT-*` scenarios, and 44 task headings. This update adds the P8.6/SA-LOOP/AT-73–AT-76 traceability; it does not verify the product.
- The user attachment remains supplemental input. The MudBlazor/Syncfusion decision below is the owner's explicit UI choice and does not change other product requirements.

## Historical pair and provenance

The prior documentation baseline was committed at `0bf4cf45abce753011803a80f029b45c8f8748dc` and described spec v1.3 / plan v2.3: spec SHA-256 `4317a3348a9571dac115d5cd41429a2535f246fc9d3f059a8b4cb2e37a1ee4a7`; plan SHA-256 `d720d77df550c5b3141c759ac5addfc809d98a1df85a131637202884320b1062`. Current versioned source pair supersedes those as the active requirements; old hashes/commit remain historical evidence and are retained in `DOCUMENT-INTEGRITY.md`.

## Owner decision: shared UI framework

| Decision | State | Actor/date | Decision | Scope and evidence |
|---|---|---|---|---|
| UI-01 | accepted | Product owner, 2026-10-26 | MudBlazor is the primary/default Blazor UI library and shared design system for Android MAUI Blazor Hybrid and web surfaces. Syncfusion is a targeted secondary library only for justified complex specialized controls. MudBlazor owns shared design tokens and visual identity. Syncfusion use is subject to version compatibility, a valid license/eligibility, Android/web interaction, styling, accessibility and performance evidence. | It does not change product requirements or authorize a specific Syncfusion component. Package compatibility, license, Android rendering and web rendering remain NOT VERIFIED. |

| UI-02 | accepted | Product owner, 2026-10-26 | Use the owner-provided SwiftAid loader GIF, with optional Lottie rendering through MudXtra's standalone `Blazor.Lottie.Player` package. This is an auxiliary animation renderer only; it does not replace UI-01, change the targeted Syncfusion rule, or alter other product requirements. | Package references, local runtime configuration, and GIF/Lottie/still-image assets are present in the current working tree; no current runtime rendering proof is claimed. GIF/Lottie creator and redistribution rights remain NOT VERIFIED. NuGet 1.0.6 reports MIT and .NET 8+; pinned repository LICENSE is MIT, while its source project declares 1.0.5. Exact package/source equivalence is NOT VERIFIED. See [UI-COMPONENTS.md](UI-COMPONENTS.md) and [RIGHTS-REGISTER.md](RIGHTS-REGISTER.md). |
| UI-03 | accepted | Product owner, 2026-10-26 | Use the root `swiftaid-icon.png` for theme colors, Gateway favicon, and Android application-icon source. | Current root/favicon/app-icon files match at SHA-256 `CA502C538415586ABE031CFCBA2B20EDF742E24002C7FB1B528F9DF5A9DF86BD`, 1,334,182 bytes, 1254×1254. Mobile launcher variants were generated in a reported Release build; device display and creator/license/redistribution rights remain NOT VERIFIED. |
| UI-04 | accepted | Product owner, 2026-10-26 | Share UI components across web and MAUI through a portable `SwiftAid.UI` Razor Class Library targeting `net10.0` with MudBlazor and Lottie; migrate shared loader/theme/card/status code into the library. Keep server SDKs/secrets out of UI code. | Implemented shared foundation: Gateway and Mobile reference `src/SwiftAid.UI`; the library contains shared theme provider/tokens, loader, page heading, body text, and service-boundary notice. Independent code and UX reviews and Gateway/Android Release builds are reported PASS. Host-specific pages/layout remain local; this is not proof every planned component has migrated. An earlier bounded public-shell Chrome journey passed Home/navigation, `/sessions` → `/report`, truthful unavailable states, the Mud shell, and 320px. Later corrected browser scripts were independently reviewed and rerun: the shared loader fixture covered GIF/Lottie DOMLoaded, reduced motion, theme switching, responsive widths, fallback, and the Gateway drawer mouse/keyboard state. The reruns confirm only those bounded component/navigation states; broader browser/product workflows, screen-reader/runtime accessibility, Android/device rendering, and full host migration remain NOT VERIFIED. Script hashes and precise scope are recorded in `UI-COMPONENTS.md`. |
| UI-05 | accepted | Product owner, 2026-10-27 | User-facing frontend copy uses plain product language and omits implementation, test, framework, and infrastructure wording. Status messages must match observed outcomes and must not claim success when an operation failed. | This instruction does not change other product requirements. Broader frontend copy conformance, device/browser behavior, and test results remain NOT VERIFIED. |

## Backend test environment policy and results

**Owner policy:** execute .NET and Python backend tests in Docker. This environment rule does not establish provider, database, or device acceptance.

The .NET test container used Docker 29.9.2 and `mcr.microsoft.com/dotnet/sdk:10.0.403`, digest `sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b`. Reported suites: Domain.Tests 48/48 PASS; Application.Tests 145/145 PASS (superseding a prior 136-pass/9-fixture-failure run); JobPublisherConfigurationTests 6/6 PASS; Integration.Tests 103 passed, 36 skipped, 0 failed. The focused MinIO filter passed 3 tests and skipped 4 provider-fixture tests. Exact wrapper and command form, and limitations, are recorded in `docs/PROGRESS.md`.

The 36 opt-in Integration cases remain **NOT VERIFIED**. They require PostgreSQL role/record/migration mutations against fixed loopback `127.0.0.1:32703/swiftaid_p21a`, or a provider fixture that launches Docker from the test process; the SDK test image contains no Docker CLI/socket. No database mutation or live MinIO/provider result is claimed.

Python worker tests used Python 3.12.15/uv 0.13.5. Pytest reported 49 passed and 2 skipped for absent NATS ACL credentials. Ruff **FAILED** with six EXE002 no-shebang findings in worker Python entrypoint/module/test files. The Python image and exact commands/results are recorded in `docs/PROGRESS.md`. These checks do not establish NATS ACL, provider, database, or delivery acceptance.
## Owner-approved service implementation choices

| Decision | State | Actor/date | Decision | Scope and source alignment |
|---|---|---|---|---|
| INF-01 | accepted | Product owner, 2026-10-26 | MinIO is the private original-file/audio/video object-store choice; Redis provides backend application cache and exact-result cache with bounded TTL; Qdrant is limited to scope-filtered AI memory/search indexing; PostgreSQL remains authoritative; NATS JetStream carries queues/events; FCM push is optional; native SQLite holds offline read models/drafts; UI assets are bundled. Provider configuration is server-side at the Gateway. | These are infrastructure choices and do not change other product requirements. The implementation plan v2.4 §3.3 says no Redis is required; this accepted owner choice adds Redis for backend application caching. Preserve that source statement and record the mismatch rather than rewriting the specification/plan. See [SERVICES.md](SERVICES.md). Current exact-source review of Gateway service configuration/DI passed, covering NATS `Jobs` binding and conditional MinIO registration. Docker `JobPublisherConfigurationTests` passed 6/6; Integration.Tests passed 103, skipped 36, and failed 0. The 36 opt-in tests remain NOT VERIFIED because they require fixed loopback PostgreSQL role/record/migration mutations or Docker launched from the test process, while the SDK test image lacks Docker CLI/socket. These checks do not establish live provider/MinIO or database behavior; both remain NOT VERIFIED. |

## Owner reaffirmation: required synchronization stack

| Decision | State | Actor/date | Decision | Scope and evidence |
|---|---|---|---|---|
| SYNC-01 | accepted | Product owner, 2026-10-27 | CommunityToolkit.Datasync is the required synchronization stack, centrally pinned at 10.1.2 with `CommunityToolkit.Datasync.Client` referenced by SwiftAid.Client, `CommunityToolkit.Datasync.Server` by SwiftAid.Gateway, and `CommunityToolkit.Datasync.Server.EntityFrameworkCore` by SwiftAid.Infrastructure. | Package pin and project references are present. Official client/server guidance and compatibility caveats are summarized below. This package/reference evidence does not implement synchronization: `SwiftAidOfflineDbContext`, authorized pull, device save/reload, and reconnect acceptance remain NOT VERIFIED. Android Release build/install evidence is recorded below. |

**Official Datasync guidance (reviewed 27 October 2026):**

- The [client guide](https://github.com/CommunityToolkit/Datasync/blob/main/docs/in-depth/client/index.md) says Blazor WASM and Blazor Hybrid offline capability is known to have issues because EF Core/SQLite do not work in those environments when running in the browser, and recommends online-only operations there. Its offline contract uses `OfflineDbContext`, configuration in `OnDatasyncInitialization`, per-entity endpoint/query settings, and entity fields `Id` (string primary key), `UpdatedAt` (`DateTimeOffset?`), `Version` (`string?` or `byte[]?`), with optional `Deleted`.
- The [server guide](https://github.com/CommunityToolkit/Datasync/blob/main/docs/in-depth/server/index.md) requires `ITableData` entities with string primary keys and automatic millisecond-precision `UpdatedAt` and `Version`; describes `TableController<TEntity>` route/repository/controller mapping; and requires `AddDatasyncServices`, `AddControllers`, and `MapControllers`. Its `IAccessControlProvider` exposes `GetDataView`, `IsAuthorizedAsync`, and `PreCommitHookAsync` per-item access-control hooks.

Compatibility and authorized sync behavior remain NOT VERIFIED. The implementation plan's P0.2 is the explicit Android Release spike; if Hybrid offline pull fails, its fallback is a native MAUI service/page. A .NET SDK 10.0.403 Android Release build passed and the fresh signed APK was installed/launched on `Pixel_8_-_API_36.1` (Android 16/API 36); force-stop and process relaunch also passed. The product UI still cannot reach `/offline-spike` and reports offline capture unavailable, so device save/reload/pull was not exercised. The Gateway table route remains unmapped and no MAUI token provider is verified. `SwiftAidOfflineDbContext`, authorized pull, offline persistence/reconnect acceptance remain NOT VERIFIED.

### P4.2 approved original-media attachment contract

**Status: APPROVED CONTRACT; implementation NOT VERIFIED.** `AttachmentId` is a non-empty `Guid` opaque locator, not a credential. The server resolves it to an immutable accepted-attachment binding. Calls use staff-only, server-derived `ActorContext`; requests contain no caller-supplied case, comment, intake, scope, object key, or public URL. Reauthorize current `ReadAttachment` permission and fail closed at reserve, status, append, complete, and range operations.

Application port methods are `ReserveAsync`, `GetUploadStatusAsync`, `AppendChunkAsync` (expected-offset compare-and-swap plus stream), `CompleteAsync`, and `OpenRangeAsync`. `GetUploadStatusAsync` and `AppendChunkAsync` are required durable tus/resume/PATCH additions beyond the methods named in the implementation plan. The contract media kinds are Image, Audio, and Video. This storage contract does not itself enable an image/video upload workflow or arbitrary document attachments; those product paths remain separately gated. MinIO details remain in Infrastructure. Guest upload awaits the P2.4 capability.

The implementation plan also requires `IUploadCoordinator.ResumeAsync(AttachmentId, CancellationToken)`; it returns authoritative server-offset status only. Approved layer placement is Contracts for shared identifier/status/transport DTOs, Application for `IAttachmentService`/resolver/range stream, and Client for `IUploadCoordinator`, with no Client-to-Application dependency. Source review PASS and the focused `AttachmentContractTests` command passed 20/20 on .NET SDK 10.0.403; this suite covers DTO/value contracts only and does not exercise coordinator behavior or transfer. Source hashes: `AttachmentTransferContracts.cs` `3C218D6D80340DF30BFE35339C12DC0DB3B43EA70E4A53AC7C61F16E009BD11D`; `AttachmentContracts.cs` `062D8D93E0ACFFC7D9018748533E4313105671B789C9CB79C35275967C57DA9E`; `AttachmentContractTests.cs` `FF60EA126CA776C9078FFFF6D9C6430D50816EAD6281AD738B843D8917E1E502`; `UploadCoordinator.cs` `730771E9330366B7E426B978121AAEADE88DA3DB3F26C2CC41E64F0DF1747332`. The adapter hash reviewed previously (`A9E4A1DF67F660A4154A9D77950AD4E38EEA3F7D54A3AEDD0E2883B7FFB201E4`) was superseded by current `MinioPrivateMediaObjectStore.cs` SHA-256 `0A3E13C7FA40A468A83BB6726A90B59347054C13DA3D8F99380852CB4809E1EE`, which passed exact-source re-review. `MinioPrivateMediaObjectStoreTests.cs` correction hash is `BA32535197917C4D7E5231033CAC23CF6A93348925E89430BF0D9B311BF199F9`; its focused Docker run passed 3 tests and skipped 4 provider-fixture tests. Live MinIO/service integration remains NOT VERIFIED. `JobPublisherConfigurationTests` passed 6/6 at SHA-256 `851C02C557334D19762417593E893F39DEF1FEB8D4F1109FB49DF746E107F378`. Older phase-two configuration hashes predate the approved `IJobPublisher` DI follow-up and are not the final snapshot. Client `ResumeAsync` compilation, transfer/session persistence, DI/API integration, live MinIO, database persistence, authorized range behavior, and Android acceptance remain NOT VERIFIED.

## Approved design assumption: organization invitations

| Decision | State | Actor/date | Decision | Scope and evidence |
|---|---|---|---|---|
| ORG-INV-01 | approved design assumption; NOT IMPLEMENTED | Product owner, 2026-10-27 | Only a current organization administrator with an independent membership-administration grant may issue an invitation. Use a high-entropy, one-time token stored as a keyed/hash digest, bound to its organization and to a verified intended contact when present. Expiry is configurable with a seven-day default; invitations are revocable. Acceptance is atomic and single-use, with idempotent replay. It may create only ordinary or pending organization membership plus an audit record; it never grants catchment, sector, sensitivity, action, assignment, or escalation permissions. Email is only a verified claim used to claim an invitation, never an identity key or merge signal. Recheck current authority at issue and acceptance, and audit issue, acceptance, expiry, and revocation. | Approved behavior design only; no implementation or test evidence is claimed. The P1.2 grant contract and P1.3 invitation verification remain pending. |

## Proposed product clarifications

PC-01 through PC-08 remain proposed absent a recorded owner acceptance. No clinical or institutional policy approval is inferred.

| ID | Conservative default in source | Owning tasks | State |
|---|---|---|---|
| PC-01 | Conservative worker/officer action permissions; request rather than grant reassignment/closure; explicit authority for official classification/publication | P1.2, P5.1, P5.3, P9.2, P10.1 | proposed |
| PC-02 | Minimal intake with explicit unknowns and observation/narrative/original-voice evidence; diagnosis not required | P1.1, P2.2, P3.1, P8.2, P11.1 | proposed |
| PC-03 | Expandable timeline command grouping with inspectable events and separate pending activity | P5.3, P11.1, P11.2 | proposed |
| PC-04 | Six core mobile and six officer modules with supporting views/panels; illustrations do not set authority | P11.1, P11.2 | proposed |
| PC-05 | Operational metric definitions and proposed device measurement targets; targets are not measured results or service guarantees | P8.4, P11.2, P12.1, P12.3 | proposed |
| PC-06 | Worker-documented receipt names reporter and confirmation method; no implied digital signature | P5.1, P8.3 | proposed |
| PC-07 | Resolved cases are history-first; reopen for new operational work; permitted corrections preserve closure | P1.1, P5.3, P11.1 | proposed |
| PC-08 | Same authorized demo officer may draft and publish only with explicit recorded review; never claim independent review | P7.3, P10.1, P11.2 | proposed |

## P8.6 approved scope addition

P8.6 is a P0 operational capability defined by spec v1.4 §15.8 and implementation v2.4. It covers durable structured multi-turn administrative reasoning and factual clarification; PostgreSQL owns run/checkpoint/turn state, while Qdrant is only a scoped retrieval/cache index. Each turn rechecks actor grants and case/source versions, uses existing NATS outbox/fencing/idempotency, is bounded by cycle/tool/budget limits, and reuses P8.3 approvals/receipts. User-visible questions and structured evidence/decision summaries are allowed; hidden chain-of-thought is never persisted or shown. Medical-advice routes remain excluded. Optional transcription and health-pattern analysis are not prerequisites.

| Contract item | Approved source requirement |
|---|---|
| Dependencies | P8.3, P8.5, P6.2, P1.2; acceptance dependency tracking does not block independent ready work. |
| Evidence | AT-73–AT-76 plus focused application/integration/Python checks and one real supported Meta multi-turn smoke with synthetic case data. Simulated and real provider calls must be distinguished. |
| Current status | P8.6 implementation, behavioral tests, restart/resume, provider smoke and UI states are NOT VERIFIED; no P8.6 result is claimed in this ledger. |

## Approved P6 source-version and result-byte contracts

**Status: APPROVED CONTRACT; source review PASS; P6 byte-vector and persistence acceptance NOT VERIFIED.** Master approved these byte-level contracts on 27 October 2026. Architecture and data-integrity source reviews passed on the reviewed P6 source snapshot. The Docker Integration.Tests aggregate reported 103 passed, 36 skipped, 0 failed; that aggregate does not establish P6-specific byte-vector coverage, persistence, or transaction behavior. The opt-in database/provider fixtures remain NOT VERIFIED. No database migration for these contracts has been generated or applied. No per-job-type schemas are enabled.

### Source-version descriptor v1

`source_version_hash` is SHA-256 of the exact `source_version_descriptor_bytes` produced by a versioned registry. Persist both the exact descriptor bytes and hash with immutable snapshot/job metadata; never include the descriptor in a NATS message.

Encoding is byte-exact: ASCII magic `SWIFTAID-SV1`; U16BE descriptor schema version; U16BE UTF-8 canonical job-type name length followed by those bytes; U16BE field count; then fields sorted by UTF-8 field-ID bytes. Each field is U16BE field-ID byte length, ASCII field-ID bytes, a one-byte type tag, U32BE value length, and value bytes. Values encode UUID as 16 RFC 4122/network-order bytes; Boolean as `00` or `01`; signed Int64 as big-endian; UTC `DateTime` ticks as Int64BE; SHA-256 as exactly 32 bytes; and closed-enum/text values as NFC UTF-8. Explicit null uses its own type tag with zero length; a missing field is never null.

Reject duplicate, unknown, and missing-required fields, as well as invalid lengths and overflow. Each `(jobType, descriptorSchemaVersion)` registry entry defines exact fields and types and the resolver/check for the current source version. If no entry exists, reject job creation, claim, publish, or completion. No job-type schema is enabled until separately reviewed; `Surveillance` remains disabled.

### P6.2 result bytes

Validate results with a strict UTF-8/JSON typed validator. The default maximum is 1 MiB and may be configured downward only. `payloadSha256` is SHA-256 of the exact accepted UTF-8 result bytes: do not normalize JSON. Whitespace or property-order changes therefore change the digest; the digest is not semantic deduplication. A duplicate for the same attempt is idempotent only when its hash is identical; a different hash cannot replace the accepted result. Discard stale result bytes and audit only redacted hashes and metadata.

Independent architecture and data-integrity source reviews passed on the reviewed P6 source snapshot, including the descriptor codec and byte-vector definitions. The latest Docker Integration.Tests aggregate reported 103 passed, 36 skipped, 0 failed; opt-in database/provider fixtures remain NOT VERIFIED. Byte-vector coverage, persistence and transaction behavior, database constraints, and end-to-end P6 job delivery remain NOT VERIFIED.
## Metadata-only source correction

The operative paired-revision text, current handoff examples, task counts, and final inventory summary were aligned to v1.4/v2.4 and 365 requirements, 76 scenarios, and seven additive tasks. This was metadata/count reconciliation only; no requirement behavior or acceptance scope changed. Historical references to the v1.3/v2.3 source basis remain intentionally identified as historical. Immediately before this metadata correction, the v1.4/v2.4 pair was observed at source revision `ef8a8cd7d6a24897310bdf6490227082d675499a`, with spec SHA-256 `8bdf616323a0d92149965b99d999f1ef235a2793812c6c87c4d203cf412ad60c` (217604 bytes) and implementation SHA-256 `2d98fd6ec3d9c019b87a4a9a4981485727b02cf695c88dbd028c1c0c13f7f76f` (430273 bytes). Current corrected hashes are in `DOCUMENT-INTEGRITY.md`.

## Source crosswalk discrepancy: P0.3 acceptance evidence

The v2.4 P0.3 task still lists AT-45 as accountable acceptance evidence. Spec v1.4 defines AT-45 as the optional open-weight bonus scenario; P0.3 separately specifies AT-14, AT-34 and AT-36 plus a real provider call for Meta capability verification. Preserve both statements pending task/owner clarification. Do not make optional open-weight execution a prerequisite for the core product. P0.1 / AT-36 remains NOT VERIFIED.

The current source pair is the authority for requirements. This ledger does not imply implementation, test, provider, device, license, clinical, rights, release, or deployment evidence.
