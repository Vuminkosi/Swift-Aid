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
(`docs/ellai/ellai-system-instruction.md`, currently version **1.6**) loads
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
2,000,000-token cumulative session budget plus a $70 per-envelope and $70 total spend cap
(dollar caps stay inert until real Meta rates are configured). Usage samples
persist per provider call in PostgreSQL `ellai_usage_samples` with priced
costs; the worker hydrates durable totals into the ledger before each run,
so token budgets and spend caps survive restarts and converge across
instances by maximum, never double-count.
**Deliberate deviation:** the requirements handoff asks for
3 iterations / 4 calls / 1 repair per cycle; the owner previously directed
the highest possible ceilings to avoid frustration, so the raised ceilings
stand and that handoff item is recorded as owner-deferred, not implemented.
The current owner-approved limits are 12 reasoning iterations, 4,000 tool
calls per request and two schema repairs. The tool-count limit is shared
by model-turn parsing, the reasoning prompt and execution accounting;
parallel execution remains bounded to four tools at once. A higher session
budget does not enlarge the bounded conversation context sent to the model.

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

The production `OrganizationsSurface`, shared by web and mobile, describes membership,
directory, creation and invitation controls through `EllaiOrganizationUiManifest`.
Discovery uses the existing fresh scope/epoch gate; it does not confer organization
administration rights. Metadata contains no membership IDs, directory values, search
text, invitation inputs, or issued codes. Control availability follows the rendered
state and saving flags. Creation, joining and invitation controls remain R3 human
operations with highlight-only bindings; observing them cannot prove a completed
membership or invitation action. Their business services and approval rules are
unchanged. Server receipt completion for those operations is not implemented by
this semantic adapter.

Direct `NavigateFeature` proposals use the shared menu registry, never a model URL.
The host refreshes authentication and observes the destination before navigating;
a collapsed but available menu is permitted, whereas disabled, absent or ambiguous
targets are rejected. The existing reveal callback may mount a menu before a bounded
re-observation. Mobile exposes the shared menu in a signed-in drawer for this and
system tours. A local back command uses at most 20 canonical registered-page/case
locations; queries and fragments are not retained, unknown locations clear the
trail, and back to a case repeats its server-authorized read. This trail is not
persisted across refresh or layout replacement and never undoes a domain action.
Destination pages retain their own data and action authorization.

Interactive case presentation reuses the shared case pages. `OpenAuthorizedRecord`
accepts only a canonical case GUID and obtains a fresh `ICaseTimelineClient` read
before generating the fixed local route; the destination rechecks access.
`SetPermittedFilter` targets a registered filter on the currently mounted case list.
The host requires a newly completed turn and a fresh server-accepted observation;
the component resolves only current unambiguous option keys/labels, validates date
ranges, resets pagination, and invokes its existing authorized query callback.
Country changes clear the prior region. Date filter arguments use `yyyy-MM-dd`
UTC boundaries. No model URL, authority flag, record write, or arbitrary UI script
is accepted. Duplicate/restored proposals and stale turn/page/session results do
not execute. A filter callback is a request, not evidence that results loaded.
These paths have automated shared-component and host coverage; live Muse/browser
conversation acceptance and mobile interaction remain unverified for this slice.


Ellai About session history opens `EllaiConversationDialog` through the shared
MudDialog provider. Desktop uses `MaxWidth.Medium`; Android/iOS and small browser
viewports use full-screen. Pinned MIT-licensed `MudX.MudBlazor.Extension` 9.10.0
provides encoded chat bubbles for durable user/assistant turns. The existing
owner-authorized response query supplies history; transient progress is excluded.
Loading, retry, empty history and terminal outcomes remain distinct. Component
tests cover completed, failed and cancelled turns and HTML encoding. Deployed
Chromium checks pass preference restoration, populated medium desktop chat and
full-screen chat at 390px. Native-device validation remains pending.

The shared composer intercepts unmodified Enter locally, preserving Shift+Enter
and IME composition, and passes the current input value through the existing
submission gate. Browser speech retries the owner-authorized staff audio route
when the guest route returns 401 or 404 (a guest cookie may coexist with a staff
login); provider failures are not retried as alternate synthesis requests.
Focused JavaScript regressions cover keyboard behavior, duplicate suppression,
audio routing and PCM cleanup. These checks do not establish live voice quality.

About also stores independent input/reply language preferences on the current
device (English defaults; Shona, siSwati, Nyanja and Tsonga choices). Typed
submissions persist these optional preferences in the existing version-1 outbox
envelope; old clients/envelopes retain English behavior. Non-English input uses
the private NLLB client before reasoning. Output translation follows a validated
decision; English provisional chunks are withheld for translated replies. A
failed output translation produces a terminal failure rather than rerunning the
tool loop. Original user text remains in canonical history. Translation quality
is unreviewed. Native-language microphone routing and MMS profile selection are
still incomplete: controls explicitly state that voice uses English, and the
audio endpoint refuses to synthesize translated text with the English voice.

Gateway history, resume, ownership and response-history reads now use independent
operation scopes, avoiding concurrent access to a circuit-scoped EF context.
The overlap regression proves separate disposed stores, not real-device capture
continuity. A missing optional UI observation no longer prevents text admission.
Typed turns additionally capture an allowlisted page identifier, excluding record
IDs and query strings. Reasoning receives its display name as advisory context
alongside the Gateway's current UTC time. This does not grant navigation or data
permissions, and no user timezone is inferred. Server-submitted voice turns still
need the corresponding page-context propagation; this remains incomplete.

Local Compose validation on 1 November 2026: 75 shared surface regressions, 21
runner/admission tests, 11 JavaScript tests, and six Gateway/language contract
tests passed (one opt-in provider test skipped). The separate real-provider HTTP
feed test completed and restored English history after reload: admission 310ms,
first text 12,444ms, durable completion 20,496ms. The same test with Shona output
completed via NLLB and restored history in 76,921ms. These synthetic measurements
do not establish native-speaker quality, low latency, acoustics or continuous
voice readiness. Commands: `node tests/SwiftAid.Ui.Tests/ellai-settings.gateway.browser.cjs`
against an authenticated CDP page, and `SWIFTAID_RUN_LIVE_PROVIDER=1 node
tests/SwiftAid.Ui.Tests/ellai-text.gateway.browser.cjs` (add
`SWIFTAID_TEST_OUTPUT_LANGUAGE=sna` for translated output).

Authenticated Chromium also verified Enter admission/clearing, a real Muse reply
identifying Ellai About as the active page, and real MMS PCM playback starting in
Web Audio (guest route 401 followed by owner-authorized staff route 200). The
guest live-voice test with a synthetic 4.144-second WAV followed by 18 seconds of
silence completed five durable turns in 106,528ms: one microphone capture, live
track at completion, ten successful response-audio requests, zero page errors,
and effective AEC/NS/AGC enabled at 48kHz. No client utterance resubmissions were
observed. This is real Gateway/provider integration with a fake microphone,
not hardware echo rejection, natural barge-in or Android/iOS validation.
The real `ellai-tool-progress.browser.cjs` rerun also passed: small talk completed
in 7,321ms without acknowledgement; the tool request acknowledged at 8,377ms and
completed at 15,583ms, with one acknowledgement, MMS playback, two restored
history turns and no stale speech replay after refresh.

Outstanding tour integration is broader than a missing prompt: the worker does
not deliver validated UI intents to a connected model-planner/Bonboarding flow;
native and guest guidance controls still return unavailable. The read-only
investigation confirmed these gaps; the changes above do not close them.

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

Observation follow-up: SPA route changes now rotate the view identity even when control IDs are unchanged; the shared client stamps web/Android/iOS platform accurately. Chromium collector regression and Android build passed. The real Android synthetic answer ultimately reconciled to a single final answer at Ready; its test session was ended. The 157-second result is a pre-repair-fix baseline, not a post-fix performance claim.

### Provider contract and evidence delivery correction (2026-11-01)

A post-repair-limit live guest HTTP test exposed TerminalFailure after 56.657s (admission 134ms; first provisional text 20.329s). This failed run is retained as evidence; bounded failure is not successful delivery. Source tracing and failing regression tests then established two provider-input omissions: only the top-level decision name was sent, without its nested schema, and tool observations omitted their structured evidence-reference objects.

The reasoning prompt now includes JSON Schema exported from the actual `EllaiDecision` contract. Permitted tool observations carry at most 20 evidence references within an 8,000-character budget, separately from their bounded prose summary. Final claim references must match evidence returned by permitted tools in the current turn; invented references trigger ordinary bounded repair. Shape/count-only rejection diagnostics exclude model prose, claim IDs and evidence payloads. Application regression tests: 330 PASS, 3 conditional skips; Gateway Ellai integration tests: 69 PASS, 20 conditional skips. `ellai-text.gateway.browser.cjs` is explicitly opt-in because it consumes provider inference; it validates actual durable outcomes and cookie-authorized history after a page reload, not rendered composer behavior.

Live rerun after the provider-contract/evidence correction: PASS, admission 134ms; first observed text and durable Completed outcome at 12.273s (two-second polling resolution); 204 assistant characters restored after browser reload with the same owner cookie. The synthetic session was ended by the harness. This single run is not a latency benchmark or proof of continuous voice, translation quality, guided-report completion or iOS behavior.

### Full Compose investigation (2026-11-01)

Live verification uses `infra/compose.yml` with classification, native-language and voice profiles running together. Disposable regression fixtures are a separate lane and do not establish deployed acceptance. Direct Gateway/Muse text/history recovery passed again (598ms admission, 12.729s terminal response). Actual MMS-TTS returned 96,256 PCM bytes in 4.737s; NLLB synthetic translation took 44.064s and MMS-ASR silent-audio inference took 20.865s. These include model loading/switching and are not language-quality or microphone tests. Real worker-to-Gateway/PostgreSQL authorization, JetStream connectivity, BART synthetic inference and Qdrant scoped recall passed. Temporary Qdrant collections and Redis keys were removed.

The signed-in browser imported two explicitly synthetic reports into `inxuros-com-general`: import `b9b332a0-6b69-4cca-9a26-bb5dd01c4026`, saved case IDs `abe8c420-facb-4410-9907-6ca67b2e0f6b` and `101d4d6d-caa3-4d8a-9bef-00eddb6b5a0d`. Both were accepted without rejected/retry rows. Retain these identified test records for end-to-end classification/reload verification; no existing imports were reset.

This live test exposed and corrected: host-published Redis/MinIO ports used as Docker-internal endpoints; an omitted ingestion-dispatch option binding that silently stopped the outbox worker; classification authorization requesting a disabled bearer handler; circuit-scoped case queries sharing DbContexts; and mandatory optional-panel injection preventing case-detail rendering. Internal ports now remain 6379/9000/4222 regardless of host remaps. Classification uses the existing conditional staff policy and retains per-case authorization. Case list/summary reads own asynchronous scopes. Missing evidence/discussion providers produce unavailable panels, not fabricated content or a failed page.

Regression evidence: the authorization, scope and configuration tests failed before correction; 48 focused integration checks and 10 case UI checks passed afterwards. After rebuilding/recreating Gateway and its network-sharing classification worker, the live classification endpoint changed from HTTP 500 to authenticated HTTP 200; real outbox dispatch and persisted classification results resumed. There were 8,012 pending case events before the fix. Backlog drain and classification of the two identified cases must be observed separately before claiming their complete workflow passed. Gateway MinIO remains disabled; service readiness is not proof of application attachment delivery. Native-language conversation routing, continuous voice and physical-device/iOS validation remain NOT VERIFIED.

### Operational maps and original report evidence (2026-11-01)

The dashboard and surveillance surfaces share a locally bundled Leaflet street map with weighted report clusters, keyboard/wheel/touch navigation, public-place popups and a country-fit reset. The country selector defaults to Eswatini by the requested product default; this does not add a country property to workspaces. A location hyperlink can select another country and resolve an unambiguous public geographic-registry reference. The displayed 100% is the selected country's fitted view, not a world-scale percentage.

`SurveillanceMapDetailsGatewayClient` reads the current workspace's authorized officer queue and rechecks `ReadCase` through the original-report client. This bounded view covers at most 500 recently updated owned cases, filtered by record creation time and country; it is not a complete population map. Only public registry coordinates are returned. Groups with fewer than five reports in either nonempty sector are withheld so layer toggles cannot reveal small subgroup counts. Missing/ambiguous places remain unmapped. Recent signal details use the existing signal review authorization, and no risk rating is invented from unknown detector severity. Country aggregates remain a distinct, broader authorized summary.

The original report panel replays protected case events within a current read lease, preserving recorded narrative and observations. Queue previews are bounded; detail views retain the full authorized evidence. Location labels link to the map without including precise report coordinates. Independent dashboard/map reads own their database scopes, preventing concurrent Blazor circuit reads from sharing a DbContext. Country geometry crosses Blazor interop as a bounded stream when the outline component is used.

Map assets and licenses are documented in `src/SwiftAid.UI/wwwroot/vendor/leaflet/README.md`. Standard OpenStreetMap tiles require connectivity; no offline tile downloads are performed. Country outlines, accessible totals and report markers remain usable when tiles fail. External tiles receive ordinary viewport requests, not report payloads. Regression coverage includes six map grouping/workspace isolation tests and 24 focused UI/client tests. Real browser and device results must be recorded separately from these automated component checks.

Live investigation found all 264 original registry entries had null coordinates. The curated seed now carries 111 unique GeoNames matches (29 SZ, 82 ZW), with archive hashes, individual source links and CC BY 4.0 attribution; 153 unresolved references remain null. `resources/catalogues/GEOGRAPHIC-COORDINATES.md` describes reproducibility and limitations. The existing `database-migrate` Compose prerequisite refreshes these references under the migration lock without rewriting case data. A `--no-deps` Gateway restart intentionally bypasses that prerequisite, so deployments using that shortcut must run `docker compose -f infra/compose.yml run --rm --no-deps database-migrate` first. Seven registry integration tests passed, including idempotent upgrades. The live service then returned Sidvokodvo's persisted public reference coordinates.

The first live marker check exposed a scope-before-limit defect: the global newest 500-case window contained 498 Zimbabwe cases and only two Eswatini cases. Map candidates now filter owner, workspace, country and creation window in PostgreSQL before limiting to 500; every candidate's report still passes its own `ReadCase` lease before contributing evidence. A dedicated disposable-PostgreSQL regression passed with a newer foreign-country case preceding the selected case. The live Eswatini view subsequently displayed five report markers, including a clickable Siphofaneni popup showing 40 reports.

Final Chromium acceptance against rebuilt `infra/compose.yml` Gateway: PASS for dashboard/surveillance wheel and keyboard zoom, 100% country reset, authenticated map API, case-detail links and original observations from both tables, location hyperlinks, 390px layouts, homepage model sections, animation pause and reduced motion. Selecting Zimbabwe and returning to Eswatini also passed. Automated gesture checks blocked external tile requests; a separate normal map render loaded OpenStreetMap imagery and verified a real popup at desktop and mobile widths. Evidence: `.codex/street-map-browser-final.log` and `artifacts/live-integration/street-map-{desktop,clickable,mobile}.png`. Gateway and classification worker were recreated and healthy. Native Android/iOS map interaction, complete workspace-wide marker coverage, and populated signal-card end-to-end behavior remain NOT VERIFIED; this test workspace returned no authorized recent signals for the selected view.

### Map case drill-through and architecture diagram (2026-11-02)

Map popups expose `View cases >>` beneath the report count. The shared `MapCasesDialog` lists only the cases in that point's authorized, suppression-checked snapshot and respects the current human/animal layer selection. It preserves the map country, creation window and 500-candidate bound rather than redirecting to the separately capped mine queue. The additive `SurveillanceMapPoint.Cases` field carries only case identity, sector, workflow and reporting time; case links enforce current case-read access again. Older responses without references do not display a dead button. No patient coordinates or new public case directory are introduced.

Marker and cluster colors represent human (blue), animal (green), or mixed (purple) report groups. The visible legend and marker titles provide non-color identification. Counts do not imply risk severity or confirmed outbreaks. Existing small-subgroup suppression is unchanged.

The homepage architecture now has 27 components and 28 animated directed connections, matching the supplied logical graph. PostgreSQL's durable Ellai outbox is distinct from NATS background jobs. Bidirectional relationships, optional local Llama reasoning and synchronization are indicated. The diagram supports pause, reduced motion, keyboard-scrollable mobile containment and a textual connection list. It is explicitly an architectural illustration, not live traffic; language quality and dynamic guidance limitations remain disclosed.

Verification: six map authorization/suppression regressions PASS (`dotnet test ... --filter FullyQualifiedName~SurveillanceMapDetailsClientTests`). Real Chromium against the rebuilt Compose Gateway opened 40 matching Siphofaneni cases and navigated to authorized case details. Homepage browser checks PASS for all nodes/connections, pause, reduced motion, unclipped titles and no page overflow at 390px. Native Android/iOS map gestures remain NOT VERIFIED; the shared implementation covers both clients. Evidence is retained under `.codex/map-cases-browser*.log`, `.codex/architecture-browser.log` and `artifacts/live-integration/`.

### Ellai history presentation and composer disposal (2026-11-02)

History now uses compact, date-grouped conversation rows with the first saved user question, accepted-message count, status and a clear chat-dialog action. `PostgresEllaiSessionStore.ListOwnedAsync` projects the first 160 characters and count in the existing bounded owner-filtered query; it does not fetch every full transcript or issue per-session network calls. Additive nullable DTO fields preserve older clients and sessions without messages. No schema migration is required. Read-only projection properties do not affect session fencing or persisted state. Refresh failures, empty history and older-page loading retain their existing states. Shared web/mobile markup opens the existing medium web/full-screen mobile MudDialog with MudX chats.

The reported `ellai-composer-drag.detach(null)` exception was reproduced before correction. Browser-owned mutation cleanup now releases interact.js and resize handlers when the element leaves the DOM; null/repeated detach is harmless and pending asynchronous attachment cannot resurrect a disposed binding. Three new regressions failed before the correction and pass afterward. The browser also completed three actual composer open/close cycles with responsive tabs and no circuit errors.

Validation against the local Compose Gateway and disposable PostgreSQL:

- PASS: `node --test tests/voice/ellai-composer-drag.test.mjs tests/voice/ellai-composer-input.test.mjs` (7 tests).
- PASS: `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter "FullyQualifiedName~SurveillanceMapDetailsClientTests|FullyQualifiedName~EllaiSurfaceLogicTests"` (81 tests).
- PASS: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~EllaiHistoryPreviewTests` with the existing disposable intake PostgreSQL fixture enabled (1 test, including long/short previews, empty sessions, pagination and owner isolation; synthetic writes rolled back).
- PASS: Chromium desktop and 390px viewport checks for map-to-case drill-through, responsive history grid, persistent language settings and conversation dialogs. Browser evidence: `tests/SwiftAid.Ui.Tests/map-cases.gateway.browser.cjs`, `ellai-settings.gateway.browser.cjs`, `.codex/composer-disposal-browser-final.log`, `.codex/map-history-browser-accepted.log`, `.codex/history-settings-browser-accepted.log` and retained screenshots in `artifacts/live-integration/`.
- PASS: Gateway image rebuilt through `infra/compose.yml`; migration prerequisite applied zero migrations; Gateway and the attached classification worker recreated healthy. The existing optional WASM native-link warning remains; offline SQLite behavior was not validated by this slice.
- NOT VERIFIED: native Android/iOS device interaction (deferred by request), full dark-theme/accessibility audit, native-language voice quality and dynamic Muse-guided tours. No production-readiness claim is made.

### Interactive map legends and facility references (2026-11-02)

The shared street map now owns an accessible button legend: Human and Animal toggle their report layers, Both toggles both together, and Medical centers & clinics toggles a separate reference layer. `aria-pressed` uses explicit lowercase string values; a static-render regression caught and corrected boolean attribute omission. Parent callbacks keep the existing aggregate presentation synchronized. The map and legend remain mounted when every report layer is off, so users can restore layers without recreating the map or losing zoom. The old duplicate checkbox row and its unused styling were removed.

The shared map now bundles 966 named Zimbabwe and 39 named Eswatini hospital/clinic OSM records from HOT's 2026-10-07 national extracts. H/C symbols and separate M-count clusters keep facilities distinct from reported cases. Attribution and incomplete-coverage limitations are documented in `resources/catalogues/MEDICAL-CENTERS.md`. The rebuild script validates country assignment, coordinates, geometry, identifiers, license and archive bounds and retains source snapshots/hashes.

Authorized case report reads now calculate the nearest mapped clinic and hospital independently from occurrence coordinates, preserving approximate precision/accuracy. Distances are Haversine straight-line kilometres, not driving distances. When coordinates are unavailable, a user-approved fallback resolves one unambiguous settlement in the reported country/region and labels distances as approximate from its mapped centre. Unknown/ambiguous places yield an explicit unavailable state. Private coordinates are never returned or put in map links. The additive `NearbyFacilities` contract is returned by the existing read query and the authenticated `/api/v1/cases/{caseId}/report-details` endpoint (private/no-store). The endpoint reuses the read lease and case authorization; denial does not reveal existence. Web and native mobile share `CaseReportEvidence`; mobile now loads this report through its existing cookie-session HTTP transport with stale-request rejection.

PASS: facility JS tests (2), application distance/locality tests (5), focused shared UI/map tests (15), case read/distance authorization tests (18), and endpoint tests (12). Android compilation passed with zero warnings/errors; no native runtime test was run. Native device testing is deferred by request. Live Chromium map checks passed for SZ/ZW coverage, keyboard toggles, all-layers-off recovery and clinic popups. A live report API check found an unregistered OpenIddict scheme in the federated deployment; the new endpoint now uses the existing SwiftAidStaffApi policy, with a no-bearer regression. A build is not device proof. Raw public catalogue archives and test/build logs are retained under `.codex/`.

The shared case-detail grid now uses a bounded track and a deep selector for MudTabs, correcting phone-width overflow found during live verification. Facility text wraps and the nearest clinic/hospital remain separate labelled rows.

Final live verification: PASS using authenticated Chromium against `infra/compose.yml`: both country facility layers (SZ 39, ZW 966), keyboard toggles, recovery with all layers off, clinic popup, phone viewport, and an existing synthetic case's nearest clinic/hospital with locality-centre label, kilometre distances, safe public map links, private/no-store response and no coordinate properties. No page errors were observed. Gateway and classification-worker were recreated and healthy; automated migrations reported Applied: 0. Android build passed with 0 warnings/errors; iOS compilation and native Android/iOS runtime are NOT VERIFIED. Screenshots remain in `artifacts/live-integration/`; public source archives and build/test logs remain in `.codex/`. The earlier live HTTP 500 and phone overflow were corrected and rechecked, not treated as passing initial results.


### Shared live-voice WebSocket transport and diagnostics (2026-11-02)

Web and native MAUI live capture use `/voice/live-stream`. The current segmented protocol is documented below; it supersedes the original continuous PCM transport. Native capture retains Android/iOS voice-communication processing. The HTTP upgrade reuses existing cookie/bearer handlers, with a same-origin check and explicit Origin. The handshake adapter maps ws/wss to HTTP/HTTPS for those handlers and requests response headers without buffering the upgraded duplex body. Native Ellai HTTP clients use SocketsHttpHandler for duplex upgrades. Continuous HTTP live capture has been removed. Bounded completed uploads use `/voice/recorded` or `/voice/recover`; status, conversation and receipt polling remain HTTP.

The existing bounded PCM queue, capture ID, replay offset and reconnect owner are preserved. PCM16 byte order survives fragmented reads; an incomplete final sample is rejected. Connection/send/close waits are bounded. A failed receiver cancels its sender without disposing the parent microphone stream. Only explicit capture EOF initiates a normal close. Gateway now acknowledges CloseReceived as well as Open sockets. No new VAD, silence suppression or microphone capture engine was introduced by this correction.

Compose diagnostics: SwiftAid lifecycle logs at Information; EF Core/Npgsql and Python database-driver categories at Error only; routine ASP.NET request polling and HTTP-client chatter suppressed. Gateway logs are UTC timestamped and single-line. Voice logs include session/capture/epoch, speech/silence durations, state transitions, periodic five-second progress, provider-final character count, admission outcome, worker pickup and durable outcome timing. No raw audio/transcript/prompt/credential logging was added. Classification logs model readiness, result identity and terminal/deferred execution status.

Verification:
- PASS: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter "FullyQualifiedName~EllaiNativeWebSocketTransportTests|FullyQualifiedName~EllaiVoiceTranscriptionServiceTests"`: 21 tests. Real loopback WebSockets cover guest/staff routes, cookie-handler invocation, Origin, capture offsets, ordered PCM/silence before EOF, lifetime beyond HTTP request timeout, disconnect, cancellation and foreign-origin rejection. Existing voice tests cover provider finals and Gateway admission while capture remains open. Initial handshake tests failed because ws/wss did not pass existing HTTP origin checks; corrected in the explicit upgrade adapter and rerun.
- PASS: `EllaiGuestReviewHttpClientTests`: 13 compatibility tests. Worker consumer/backfill tests: 8 passed using `workers/.venv/Scripts/python.exe -m pytest workers/tests/test_classification_consumer.py workers/tests/test_classification_backfill.py -q`.
- PASS: Android compilation (`dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -f net10.0-android --no-restore -m:1 -nr:false -p:WasmBuildNative=false`), zero warnings/errors. No emulator installation or device operation was performed.
- PASS: real Chromium synthetic speech through Compose Gateway/Muse: session `ellai-618edc55c4ef4acea2a191d90394b33d`, one capture, 444 frames, one durable completed response, response-audio request, live track retained, AEC/NS/AGC reported enabled, no page errors. Silero endpoint reached 480 ms silence; the completed turn snapshot showed 1,280 ms silence. Full response completed at 17,223 ms from capture start; this is functional evidence, not a low-latency claim.
- PASS: shared native WebSocket client exercised on Windows with synthetic PCM against the real Gateway/Muse: session `ellai-3131c9a691ba4528afeb5f190c578362`, one completed voice turn and durable response at 18,410 ms, input stream still open. This is NOT Android/iOS runtime validation. Probe source, synthetic fixture and logs are retained under `.codex/ellai-native-transport-probe` and `.codex/ellai-*-*.log`.
- PASS: Gateway and classification worker rebuilt/recreated; automated migrations Applied: 0. Live logs showed endpoint, provider final, accepted admission, worker pickup and Completed outcome. No database-command entries appeared in the checked window.
- NOT VERIFIED by the original transport slice: physical-mobile failure resolution, native microphone behavior, iOS build/runtime, acoustic echo rejection and physical barge-in. User owns mobile interaction testing. The subsequent client Silero implementation is described below.

### Client Silero freeze baseline — 2026-11-02

Web and Android use the same pinned Silero v5 model family on existing enhanced microphone capture. Browser inference uses `@ricky0123/vad-web` 0.0.31 and ONNX Runtime Web 1.22.0; Android uses native ONNX Runtime. Vendored asset hashes and licenses are in `src/SwiftAid.UI/wwwroot/vendor/vad/ASSETS.md`. No additional capture owner or custom speech DSP is introduced. Microphone RMS drives the visual glow only.

`swiftaid.voice.segmented.v1` carries ordered audio/speech/heartbeat events with original sample positions and stream epochs; the authorized socket binds session and capture identity. Both gates retain 800 ms pre-roll and real trailing silence. The requested pause is 700 ms (16,800 samples at 24 kHz), evaluated on 32 ms frames, yielding a 704 ms threshold. This is an acoustic threshold, not a guarantee of total response latency. Idle PCM is suppressed while the socket and microphone remain active. iOS retains its existing detector and sends framed audio without this new client silence suppression.

Each speech segment closes its individual Muse transcription request, not the parent conversation or microphone. Gateway Silero remains authoritative, and a validated final transcript is required for completion and durable reasoning admission. Client transcript polling does not gate admission. Stable turn IDs derive from session/capture identity and Gateway acoustic spans to preserve replay idempotency. Missing sequences, stale epochs and overlapping PCM fail recoverably instead of fabricating continuous audio. Counters track audio bytes, suppressed samples, duplicate/missing events and existing endpoint latency.

Freeze verification: 37 focused integration tests, 16 client tests, 21 coordinator tests and nine JavaScript tests passed. Gateway image build and recreation passed; Gateway and classification worker reported healthy, and the served asset contains the 16,800-sample threshold. Android embedded build passed with zero warnings/errors; installation and activity launch succeeded after an explicitly authorized emulator data wipe. No interactive mobile test was performed.

Earlier real-provider browser runs passed two turns on one microphone capture, 62 seconds of silence with zero PCM upload, and injected WebSocket disconnect/replay with two completed turns. Those runs used the earlier 1.5-second threshold: equivalent end-to-end results at 704 ms remain NOT VERIFIED. Acoustic echo rejection, physical-device barge-in, iOS runtime, real process-restart replay and spoken tool acknowledgement remain NOT VERIFIED live. Existing deterministic tool acknowledgement is retained unchanged. Further voice behavior changes are frozen pending explicit authorization.

### Shared case evidence and composer status presentation

Case evidence uses responsive paired panels for original observations and nearby mapped facilities. Facility distances retain their locality-centre estimate, straight-line distance and unverified-service qualifications; presentation does not change facility categorization or referral suitability.

Every Ellai status uses a decorative three-bar animation, including unknown/fallback labels. Connecting/reconnecting use a quicker wave; Thinking retains purple text and bold animated dots; other states use a gentle pulse. Reduced-motion preferences disable both animations. The shared component covers web and MAUI without changing voice lifecycle behavior.

Verification: `case-evidence-status.browser.cjs` passed in Chromium at 1280px/light and 390px/dark with ten status labels, map link attributes, responsive panels, no horizontal overflow, reduced motion and no page errors. `ellai-microphone-toggle.browser.cjs` passed at 1280, 390 and 320px. These are synthetic shared-component browser checks, not physical-mobile or live case-data validation.

### Ellai navigation identity and classification ordering

Production UI discovery now checks the current PostgreSQL actor status, expiry and access epoch through `IEllaiUiIdentityAuthority`. The case-bound grant adapter requires an active case transaction and cannot establish menu discovery authority outside one. This metadata-only authority grants no case reads, mutations or approval receipts; destination APIs and record navigation still authorize their own resources. Missing, revoked, expired or stale identities cannot receive signed-in navigation metadata. Home is a registered menu destination shared by web and mobile.

Case classification evidence is ordered by result creation time descending (association time breaks ties); the shared case panel selects the newest result rather than prioritizing an older current-configuration result. Existing configuration and translation-review labels remain visible.

Focused verification: 100 application tests passed for UI authority, navigation payloads, manifest routes and guidance admission. The reported signed-in session had an active, unexpired PostgreSQL actor at epoch 0; the previous scope resolver depended on the case-lease-only adapter. Real signed-in tour execution after this correction remains NOT VERIFIED.
