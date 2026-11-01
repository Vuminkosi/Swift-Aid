# SwiftAid Technical Reference (Ellai runtime)

Current ingestion implementation, event contracts, rollout and measured verification are maintained in
[Gateway processing architecture](architecture/gateway-processing-architecture.md). Publication and
processing completion are separate states; refer to its audit and performance report for current limits.

**Status: DRAFT — pending maintainer review.** This file is the intended
approved source for Ellai's `ellai_docs_search` / `ellai_docs_get` tools and
for the documentation-first reasoning contract. Until a maintainer reviews
and approves a revision, Ellai must treat entries here as provisional and
prefer live authorized reads for current state.

Section status vocabulary: **Verified** (ran against the cited code/tests
this revision), **Implemented** (code present, unit-covered),
**Stub** (in-memory where durable authority is required), **Missing**.

## 1. Runtime policy pipeline

**Status: Implemented.** The approved LLM policy file
(`docs/ellai/ellai-system-instruction.md`, currently version **1.5**) loads
once at gateway startup via `EllaiRuntimePolicy.Load` / `ResolvePath`
(`src/SwiftAid.Application/Ellai/EllaiRuntimePolicy.cs`). Every guarded
reasoning call carries the exact text as its system message, stamped with
version and content hash (`EllaiPromptBuilder`). Load failure,
engineering-content leak, or hash mismatch fails closed: no model call runs
without the pinned policy. In Docker the file is a read-only bind mount
(`SWIFTAID_POLICY_FILE` → `/etc/swiftaid/policy/...`), never baked into the
image or DLL. Pinned version defaults live in `EllaiOptions.PolicyVersion`
(`src/SwiftAid.Intelligence/Meta/EllaiOptions.cs`) and
`infra/.env.example` (`Ellai__Policy__Version`).

## 2. Tool broker catalogue and bounds

**Status: Implemented, unit-verified.** `EllaiToolBroker`
(`src/SwiftAid.Application/Ellai/Tools/EllaiToolBroker.cs`) is read-only:
the model chooses reads or emits a decision; it never receives
execute/approve/sql/shell.

| Tool | Purpose |
|---|---|
| `ellai_state` | Minimized feature projection; `projection=queue` runs the ordinary scoped "my cases" query for the trusted actor. |
| `ellai_evidence` | Bounded authorized source items and versions. |
| `ellai_topic` | Session topic candidates with checkpoints. |
| `ellai_capabilities` | Current capability descriptors and readiness. |
| `ellai_receipts` | Live receipts from the durable authority: one by id or the session list, session-bound, absence as authorized empty. |
| `ellai_topic` | Live topic-ledger resolution: ordinals first, ambiguity explicit, unknown as authorized empty; durable in PostgreSQL `ellai_topics`. |
| `ellai_productfacts` | Versioned nonmedical product facts and help. |
| `ellai_ingestion_schema` | Current case CSV/capture requirements and receipt/authority boundaries; does not submit data. |
| `ellai_validate_observations` | Validates the existing string-array observation format with bounded input; returns format/count only, without clinical interpretation or submission. |
| `ellai_locations` | Bounded public geographic registry search with versioned provenance; candidates require user selection. |
| `ellai_import_destinations` | Current actor-authorized organization/workspace/country destinations, at most 20; directs staff to Workspace Imports without submitting anything. |
| `ellai_imports` | At most 20 actor-owned recent imports for an explicit organization/workspace/country, with current membership and access checks. |
| `ellai_import_status` | Durable actor-owned import counts, checkpoint and state; no original source content. Import completion does not establish intelligence-processing completion. |
| `ellai_classification_evidence` | Persisted semantic scores, source-event references, language, model/taxonomy versions and processing outcomes for an authorized current case revision. Rechecks current case access; never runs inference or changes clinical classification. |
| `ellai_now` | Current server time (UTC, round-trip + unix ms); no domain data. |
| `ellai_docs_search` | Search this approved reference by topic/section id. |
| `ellai_docs_get` | Retrieve one approved section excerpt with provenance. |
| `ellai_ui_catalogue` | Permission-filtered semantic catalogue slice (`goal`, `limit`); descriptors requiring capabilities stay excluded fail-closed. |
| `ellai_ui_observe` | Live observation over the client-posted mounted-control channel (`controls`, `view`, `context`); Ready only for reported-mounted entitled controls, marked `ClientObservation` with server-stamped freshness; missing report is an authorized empty. |

Compatibility aliases (accepted, metered, never advertised):
`ellai_voice_capability` → `ellai_capabilities`,
`ellai_guidance_session` → `ellai_state`. Retired names
(`ellai_caption`, `ellai_guidance_anchor_catalogue`) have no backing
projection and are rejected as unregistered.

Uniform bounds per call: at most 20 items, at most 32,768 serialized UTF-8
bytes of the wire envelope (multibyte/escaped text counts as sent), at most
8 arguments of 120 chars, code-shaped arguments rejected. Machine-readable
reason codes (`Ok`, `UnregisteredTool`, `BudgetExceeded`, `MissingScope`,
`InvalidArguments`, `ConsentMissing`, `DeniedByPolicy`, `ResultTooLarge`,
`Unavailable`) accompany every denial.

## 3. Consent and budgets

**Status: Implemented, unit-verified.** Every broker call requires a live
`ReasoningText` grant for purpose `ellai-tool-read`; a missing consent
service fails closed. Grants persist in PostgreSQL `ellai_consent_grants`
with authenticated grant/revoke endpoints; the worker hydrates durable
grants into the hot path before each run, so restarts and sibling instances
converge (revocation only moves toward revoked). The cycle runner (`EllaiCycleRunner`) enforces
25 reasoning iterations, 60 model-visible tool calls and 5 model-only
schema repairs per cycle with single-flight per investigation. The runner
publishes correlated activity events (`EllaiActivityStatusCoordinator`:
accepted, turn, tool-started/observed, approval, verification, completed,
blocked, cancelled) with session/epoch/sequence fencing; the composer
status line binds to these over the journal `activity` transport. The
accepted check-in fires on the first real tool request only, so pure
small talk emits no activity; completion projects only after tool work,
blocks always. The per-iteration gate (`EllaiReauthorization`) carries a
budget/denied detail for honest terminal status. Tool requests carry a
model-assigned call id (`EllaiToolRequest.CallId`) that survives
validation/execution for next-turn correlation. The `EllaiUsageLedger` enforces a
200,000-token budget plus a $70 per-envelope and $70 total spend cap
(dollar caps stay inert until real Meta rates are configured). Usage samples
persist per provider call in PostgreSQL `ellai_usage_samples` with priced
costs; the worker hydrates durable totals into the ledger before each run,
so token budgets and spend caps survive restarts and converge across
instances by maximum, never double-count.
**Deliberate deviation:** the requirements handoff asks for
3 iterations / 4 calls / 1 repair per cycle; the owner previously directed
the highest possible ceilings to avoid frustration, so the raised ceilings
stand and that handoff item is recorded as owner-deferred, not implemented.

## 4. Sessions, approvals, receipts

**Status: Implemented, unit-verified (sessions and approvals durable).**
Sessions persist in PostgreSQL (`EllaiSessionEntity`, `EllaiCheckpointEntity`,
`EllaiUtteranceEntity`, `PostgresEllaiSessionStore`) with version-fenced
compare-and-set and idempotent utterance/checkpoint identities; restart and
second-gateway visibility hold. Accepted public product-help utterances use
`EllaiPublicHelpClassifier` to reduce typed input locally to approved product
topics. The composer obtains explicit session-scoped `ReasoningText` consent
for the fixed `ellai-public-product-help` purpose before submission; the
endpoint assigns `consented-nonsensitive` only for that fixed purpose and
dimension. The classifier remains mandatory, so consent never permits
arbitrary or sensitive text. `EllaiUtteranceWorkEnvelope` stores only the
sanitized topic prompt, fixed `ellai_productfacts` tool allowlist and
server-captured actor/session fences in the same transaction as the session
version update. `EllaiUtteranceWorker` claims this PostgreSQL outbox with
leases, preserves per-session order, checks the current owner/session/scope
epoch before provider dispatch, retries transient failures up to three times,
and clears payload text when work completes or is terminally rejected. The
`20261031062124_EllaiUtteranceOutbox` migration adds this state. Its migration
application and real PostgreSQL worker recovery remain **NOT VERIFIED** while
the Docker database is unavailable. Anonymous public help is implemented on
`/api/v1/ellai/guest`: `GET .../antiforgery` issues a request token;
`POST .../sessions` accepts Ask mode only and returns a two-hour, server-signed,
HttpOnly, SameSite=Strict guest identity cookie with an empty workspace scope.
Session-bound consent, typed utterances, event reads and cancellation require
that cookie plus antiforgery validation. Guest writes are rate-limited to 10
per IP per minute and reads to 120 per IP per minute. The WebAssembly public
root renders the shared compact launcher in public-help-only mode. No guest
case, receipt, organizational, approval or generated-navigation tools are
registered. The API/UI path and cookie isolation pass TestServer and HTTP
client tests; browser cookie behavior and the live Docker/provider journey
remain **NOT VERIFIED**. Anonymous public pages rendered through Blazor Server
also expose the same Ask-only launcher. Their guest actor is server-created
and scoped to the Blazor circuit; the scope manifest has no workspace grants,
and guest UI-observation writes are denied. This in-process path shares the
sanitized submission and consent services and passes the focused anonymous
gateway-client integration test. The browser-rendered Blazor Server journey
remains **NOT VERIFIED**. MAUI `MainLayout` now renders the shared host and an
Ask-only launcher. Its authenticated HTTP client carries native bearer or the
existing password-session cookie; its separate guest client relays the
antiforgery and signed guest cookies through that same cookie store. Each
accepted guest session remains pinned to guest routes if account state later
changes. The Android source build passes, but cookie behavior on-device and
the Mobile guest/provider journey remain **NOT VERIFIED**. Dynamic guidance
control is not exposed on Mobile until a remote control contract exists.
Replaying an accepted (session,
client-utterance) identity returns the current view without consuming
another cycle or re-triggering reasoning (§7.1). Approvals carry a 15-minute expiry and idempotent durable receipts
(`EllaiApprovalService` over `IEllaiApprovalStore`, PostgreSQL
`ellai_plan_approvals` with unique idempotency-key and plan/action
constraints) with an Accepted / AcceptedUnverified / UnknownOutcome /
PartiallyApplied / Applied / Failed lattice. A server-side dialog
coordinator enforces one active review per session, edit-then-new-version
invalidation, and terminal cancel fencing; the approval endpoint binds
dialog-issued submissions to their open review (409 when stale). Review
register/edit endpoints exist and publish to the session event journal;
clients long-poll `GET .../sessions/{id}/events` with a cursor for
approval-required/resolved/edit, session-cancelled and activity hints (gap
recovery by replay; replay never replays commands). Reviews carry safe display
rows derived from the exact reviewed proposals with stable action ids;
`GET .../approval-reviews/active` serves the open review, `POST .../approve`
approves only the human-selected ids resolved server-side against those
proposals (unknown ids reject the batch), and the client `EllaiEventPoller`
dedupes repeat deliveries so one review opens one dialog. The guidance host
starts its own durable session through `IEllaiReviewClient.StartSessionAsync`
(no layout plumbing), tracks it, polls its journal, and auto-opens
`EllaiApprovalDialog` on validated reviews (approve/reject/edit/cancel/dismiss
through `IEllaiReviewClient`; Gateway serves it in-process with per-call
principal resolution, HTTP clients use the same port). While the dialog is
open, active guidance pauses through the ordinary Pause/Resume control seam
and resumes on close; the composer status reads Waiting for your approval,
then Approval recorded — verifying / Verified / Rejected / Review dismissed /
Editing the plan. Approval responses re-read the durable authority once
(`GET .../receipts`, statuses as names on the wire): every selected id must
reconcile with a definitive status or the UI reports receipt check pending —
recording, never dispatch success. The `EllaiApprovalDialog` component renders
Approve-selected/Edit/Reject/Cancel with selection-gated approval and
version-change reset. Cycle progress reaches the status line as journal
`activity` events (projected by `EllaiActivityStatusCoordinator` from real
runner events, never model text). Still **Missing**: the atomic per-action
domain executor with outbox commit (**Blocked**: no backing
administrative-task, assignment-request or reminder domain services exist to
reuse — inventing them here would be new product surface, not integration),
and cross-instance dialog fencing.
HTTP: `POST /ellai/v1/sessions/{id}/approvals`,
`GET /receipts`, `POST /ui-observations`, utterance submit, terminal cancel
(`src/SwiftAid.Gateway/Endpoints/EllaiEndpoints.cs`). Approval is granted
only by operating the authenticated approval dialog (Approve selected /
Edit / Reject / Cancel); text or voice consent is never approval.

## 5. Dynamic UI Intelligence and surface

**Status: Partial implementation. Report journey descriptors are registered at
Gateway startup; complete model-generated guidance is not verified.**
`EllaiUiRegistry` holds versioned semantic descriptors. `EllaiGuidancePlanValidator`,
`EllaiBonboardingCompiler` and `EllaiGuidanceSessionCoordinator` provide validation,
compilation and coordination components, but their presence does not establish a
connected Muse planning-to-rendering workflow. The current draft composer is
deterministic; it does not call Muse. The single compact composer (`EllaiCompactComposer`) attaches
under the active Bonboarding card; `EllaiStatusLine` projects activity;
`EllaiPlanReview` renders the approval card; `EllaiLauncherMenu` is the one
shared launcher on every layout for guest and signed-in users; Settings/About
hosts history with per-session cost captions. The live observation channel
is real end to end: clients post mounted-control reports to
`POST .../sessions/{id}/ui-observations` (also via `IEllaiReviewClient`,
in-process and HTTP), the server keeps the latest per session in
`IEllaiUiObservationStore`, and `ellai_ui_catalogue` / `ellai_ui_observe`
read the registry plus that channel through the same consent, budget and
byte-bound pipeline as every tool. An absent report yields an authorized
empty, never a fabricated view. The public report journey descriptors
(`EllaiReportGuidanceDescriptors`, feature `report`, nine controls from
`report.entry` to `report.receipt`, submit human-owned R3) are seeded at
Gateway startup; capability-gated descriptors stay excluded fail-closed
(no scope-capability source yet).

## 6. Voice, reasoning routes, memory

**Status: Implemented, route-gated.** Meta ASR upload authenticates via the
`Authorization` header; realtime ASR authenticates in the WebSocket
handshake frame. Reasoning backends: `meta` (default hosted API), `ollama`
(local-first, `llama3.2:3b`), `serverless` (locked disabled until the Meta
default is fully tested). Custom speech synthesis is disabled pending exact
MMS-TTS checkpoint/hash and deployment, attribution, and distribution review.
Owner direction is Gateway-hosted synthesis streamed to Web/Mobile; clients
capture/play audio but do not run local TTS or OS voice fallback. Noncommercial
use only is approved. Text/captions remain available.
The multi-turn loop
(`EllaiReasoningLoop` over `EllaiCycleRunner`, `IEllaiTurnCompletionClient`
adapted from the routed provider) parses each text turn to typed tool calls
or a validated final decision through `EllaiModelTurnParser` (single JSON
object, canonical/alias names, string args, call ids); accepted tool results
re-enter the next turn under their call ids, usage accrues to
`EllaiUsageLedger`, and the per-iteration gate stops the run with a
budget/denied detail. Pure small-talk turns emit no activity; completion
projects only after real tool work, blocks always. Accepted utterances
trigger background reasoning: `POST .../sessions/{id}/utterances` records
the view and enqueues an `EllaiUtteranceWork` item (optional client
`FeatureId`/`ResourceId`, default feature "general"); the
`EllaiUtteranceWorker` hosted service runs one loop per session at a time
(latest utterance wins, queue single-flight per process) with ledger,
activity and journal wiring, and progress/results reach the UI through the
journal transport. Reasoning-text consent gates every run: grants are
recorded via authenticated `POST .../sessions/{id}/consents` (closed
purpose set `ellai-tool-read`, 1..24h TTL) and missing grants journal the
blocked template with zero model calls. Memory: PostgreSQL is
authoritative for records and provenance; Qdrant (`EllaiMemoryIndex`) is a
private derived index of scoped references only — never secrets, audio, or
the case database. Still **Missing**: cross-instance run fencing (shared
Redis lock; gate is per process) and session-to-actor ownership binding
(pre-existing: session endpoints authenticate but do not prove session
ownership — reported, not introduced here).

## 7. Verification map

Run `dotnet test tests/SwiftAid.Application.Tests --filter
FullyQualifiedName~Ellai` (unit/contract, 192 passing, 3 skipped) and
`dotnet test tests/SwiftAid.Ui.Tests --filter FullyQualifiedName~Ellai`
(51 passing). PostgreSQL integration tests
(`tests/SwiftAid.Integration.Tests`, `EllaiPostgres*`) run only with
`SWIFTAID_RUN_SQLAUTH_FIXTURES=1` and the approved disposable loopback
connection (127.0.0.1:32703); otherwise they skip. Provider, web and
Android smoke checks are recorded separately and never mocked.

### Model-backed guidance planning adapter

`EllaiModelGuidancePlanner` calls the existing routed `IEllaiTurnCompletionClient` and returns a strictly parsed draft plus provider usage. It bounds context and response size, rejects unknown JSON members and numeric enum values, preserves the requested objective/mode, and rejects malformed steps. Twelve focused planner/decision-contract tests pass with synthetic completions; no real Muse inference was exercised by those tests.

This adapter is not yet admitted from the durable utterance/session path or connected to Bonboarding activation. Its caller must enforce session authorization and cancellation fences, charge returned usage, validate registry scope and current observations, persist the accepted plan, and publish it for the renderer. It is not a completed dynamic-guidance feature. NLLB/ASR, durable translation registry and translation-derived classification remain outstanding.

### Private native language provider (2026-11-01)

The optional `ellai-native` Compose profile exposes one private language service to Gateway (`EllaiLanguage:Endpoint` / `InternalKey`); no host port is published. It reuses the speech worker's pinned Python environment. Provision `scripts/ellai/native-models.lock.json` with `provision_native_models.py` before startup. The model mount is read-only and startup validates all selected NLLB/MMS-ASR artifact hashes. The service refuses readiness if the private credential is absent.

`POST /v1/translate` accepts requestId, text, sourceLanguage and targetLanguage; supported mappings are eng/sna/nya/tso/ssw and exactly one side must be English. Inputs beyond 512 model tokens are rejected, never silently truncated. `POST /v1/transcribe` accepts requestId, language (sna/nya/tso), sampleRate=16000 and base64 mono signed-16-bit PCM (0.1 to 60 seconds). Silence can legitimately produce an empty transcript. Neither endpoint downloads models. Both return immutable model provenance and quality=unreviewed; these are not human verification receipts.

One nonblocking owner covers model loading, tokenizer language, ASR adapter switching and inference. At most one large model is resident; competing requests get 429/Retry-After. HTTP bodies and results are bounded; validation responses do not echo source text or audio. The Gateway client validates identity, language, revision, bounded output and timings, propagates caller cancellation, and distinguishes retryable availability failures. Synchronous CPU inference already underway may finish after the caller disconnects, retaining the lock until it ends; cancellation does not release model ownership early. No inference retry or database transaction is hidden inside this client.

Verification: 17 Python tests plus 4 subtests passed; six Gateway client tests passed with a real loopback provider, including actual pinned NLLB translation and MMS-ASR execution. Initial HTTP smoke observed 8.47 seconds for a short translation and 13.31 seconds for one second of ASR silence, including model switching/loading. Compose config validation passed. These checks are CPU-only, synthetic and do not establish translation accuracy, speech recognition accuracy, deployed Docker networking or client voice continuity. Session routing and durable translation/classification admission are separate integration work; registering the client alone does not enable native language capabilities.

### Experimental protected translation registry and reviewed classification bridge

`ExperimentalMultilingualIntelligence` is disabled by default. Admission requires Enabled=true, ResearchModeOnly=true, RequireHumanReview=true, environment Development or Test, and an explicit Organizations allowlist. Review additionally requires an explicit Reviewers actor-ID allowlist and current case read authorization. These restrictions are server-enforced; a client flag, spoken approval or model output cannot approve a translation. No production deployment or native-language quality claim is implied.

The additive TranslationIntelligenceRegistry migration creates only `swiftaid_translation` tables. Fingerprints include organization, workspace, sensitivity, normalized source hash, language pair, immutable model revision and inference/preprocessing configuration. Sources and translations use scoped data protection. Completed output, source provenance, source associations and review history are immutable in PostgreSQL. Mutable review state has an optimistic version and append-only decision receipts. An expired inference lease can be reclaimed; stale completion cannot overwrite the replacement; completion retries must match the original lease and result hash. Provider inference happens outside database transactions. Attempts are bounded at five, with durable retry/terminal outcomes. Disabling the feature pauses admissions, processing and pending review replay without deleting historical evidence.

Existing native-language BART abstention remains intact while a translation is pending or unreviewed. Reviewed artifacts produce a separate translation-aware classification configuration containing artifact ID, fingerprint, source language, model revision and review version. Revocation during classification fails that attempt. The existing direct-English surveillance configuration does not select these experimental translated results. This implementation requires human review before BART; it does not admit unreviewed provisional classifications.

Review commits a metadata-only PostgreSQL outbox entry for every associated immutable source event. The existing ingestion worker publishes it to the existing case subject with a fresh outbox message identity, retaining the original source event inside the notice. This reuses the existing durable classification consumer and its ACK semantics. No narrative or translated text is published to JetStream. Publication has lease fencing and bounded attempts; an exhausted publication retains its failure code for operator reconciliation.

Authorized readback and explicit review endpoints are `/api/v1/cases/{caseId}/translations` and `/api/v1/cases/{caseId}/translations/{artifactId}/reviews`. Cookie writes require antiforgery; mobile bearer requests retain normal token authentication. The shared case-detail panel shows original and translated text, structural findings, review status and reviewer-only controls. Translated classification results are labeled experimental. Backfills use the existing classification backfill path; no CSV re-import is required. Translation-specific backfill summaries and Ellai translation-review tools remain follow-up integration work.

Validation on a task-owned disposable PostgreSQL database: 13 translation/classification regression tests passed, covering source preservation, protected output, repeated completion/review, concurrency, lease expiry, restart, immutable artifacts, scope policy and review revocation. Five application invariant tests passed. Gateway and Android builds passed. Chromium synthetic-fixture review tests passed at 1280px and 390px, with keyboard submission, read-only state, conflict handling, no horizontal overflow and stale-response rejection. The first browser run exposed a fixture prerender race; waiting for actual interactive mount resolved it. Live deployment, actual JetStream review replay, real BART translation input, signed-in browser/mobile review, iOS and fluent-language quality remain NOT VERIFIED.

### Ellai live UI observations (2026-11-01)

The shared web/MAUI composer collects bounded report-control identity and effective state before typed submission and every ten seconds while the session is active. The shipped collector reads no input values, DOM text or URLs. Reports use the existing session-owner checks; guest HTTP posts retain cookie ownership and antiforgery validation. Empty reports clear prior targets. The optional `controlStates` field preserves older request compatibility; mounted-only legacy reports are `Unknown`, never proof of readiness. Storage is ephemeral, bounded to 4,096 sessions, and loss after restart prevents guidance admission rather than fabricating state.

`ellai_ui_observe` preserves server receipt time and marks reports older than 45 seconds, future-dated reports, and view mismatches stale. Dynamic plan validation requires fresh, visible/enabled, version-matching observations; registered objectives/facts; and a connected acyclic step sequence. R3 targets permit human guidance only, never generated execution. Observations never establish permission, submission or receipt completion.

Verification: 325 Ellai application tests PASS (3 conditional tests skipped); 21 Gateway authorization/metadata tests PASS; Gateway and Android builds PASS. `node tests/SwiftAid.Ui.Tests/ellai-observations.browser.cjs` PASS in Chromium against a synthetic DOM fixture (ready/hidden/disabled/duplicate/removed controls, context changes and no input capture). These checks do not establish a complete Muse-generated Bonboarding walkthrough or physical Android/iOS interaction. Those remain NOT VERIFIED and the generated-plan delivery connection remains in progress.

### Ellai model-repair reliability and local runtime validation (2026-11-01)

Actual Android emulator testing exposed duplicated provisional answer text and a slow synthetic public-help request. PostgreSQL recorded that request Completed with 164 response characters after 157 seconds. Five model calls had already occurred at 106 seconds. This proves eventual delivery/persistence, not acceptable latency or the cause of every repeated call.

Regression tests independently reproduced a repair accounting defect: `EllaiReasoningLoop` converted rejected decisions into empty turns, bypassing `EllaiCycleRunner`'s two-repair allowance. Rejected turns now explicitly consume that shared allowance and exhaust with `InvalidDecision`. Once a streamed provisional answer is rejected, subsequent model-only repairs await durable final reconciliation instead of appending another draft to the same response. The first ordinary response still streams. All three new regression cases failed before correction; the Ellai application suite now passes 328 tests (3 conditional skips). Live latency after this fix remains NOT VERIFIED.

Local deployment of commit `2de5faa` rebuilt/recreated Gateway and applied the Translation Registry migration automatically (one migration applied). Gateway, classification worker, MMS-TTS and the private language worker were healthy. Recreating classification alongside Gateway is required: its loopback-only API credentials depend on `network_mode: service:gateway`; the old worker had retained a replaced Gateway's network namespace and failed NATS DNS resolution. Preserve the classification model volume. The private service key is stored only in ignored local configuration.

Real private-container smoke results: pinned NLLB English-to-Shona synthetic translation 32.29s including load; pinned MMS-ASR Shona adapter on one second of silence 17.05s including model switching, empty transcript. Both report quality `unreviewed`. Gateway-to-language Compose connectivity and readiness passed. These measurements do not validate meaningful speech recognition or fluent translation quality.

`ellai-observations.gateway.browser.cjs` passed against deployed Gateway in Chromium: synthetic guest creation, shipped JS module, empty observation admission, invalid-state/CSRF rejection and session cancellation. Android rebuilt APK was installed on emulator-5554 with existing app data preserved; its actual WebView report controls returned Ready=4 and opening the launcher then Ask displayed the composer at Ready. An initial automation click incorrectly targeted a closed menu; opening the activator resolved it without a CSS change. iOS, physical-device voice continuity and the complete Muse-generated guided report flow remain NOT VERIFIED.
