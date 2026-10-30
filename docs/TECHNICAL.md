# SwiftAid Technical Reference (Ellai runtime)

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
service fails closed. The cycle runner (`EllaiCycleRunner`) enforces
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
(dollar caps stay inert until real Meta rates are configured).
**Deliberate deviation:** the requirements handoff asks for
3 iterations / 4 calls / 1 repair per cycle; the owner previously directed
the highest possible ceilings to avoid frustration, so the raised ceilings
stand and that handoff item is recorded as owner-deferred, not implemented.

## 4. Sessions, approvals, receipts

**Status: Implemented, unit-verified (sessions and approvals durable).**
Sessions persist in PostgreSQL (`EllaiSessionEntity`, `EllaiCheckpointEntity`,
`PostgresEllaiSessionStore`) with version-fenced compare-and-set and
idempotent utterance/checkpoint identities; restart and second-gateway
visibility hold. Approvals carry a 15-minute expiry and idempotent durable receipts
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
then Approval recorded — executing / Rejected / Review dismissed / Editing
the plan. The `EllaiApprovalDialog` component renders
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

**Status: Implemented, unit-verified (observation channel live; no
production descriptors registered yet).** `EllaiUiRegistry` holds versioned
semantic descriptors; `EllaiGuidancePlanValidator`, `EllaiBonboardingCompiler`
and `EllaiGuidanceSessionCoordinator` plan, compile and activate one guidance
at a time. The single compact composer (`EllaiCompactComposer`) attaches
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
empty, never a fabricated view. Production descriptors are not registered
next to owning features yet, so live catalogue/observe reads are
authorized-empty until that registration lands; capability-gated descriptors
stay excluded fail-closed (no scope-capability source yet).

## 6. Voice, reasoning routes, memory

**Status: Implemented, route-gated.** Meta ASR upload authenticates via the
`Authorization` header; realtime ASR authenticates in the WebSocket
handshake frame. Reasoning backends: `meta` (default hosted API), `ollama`
(local-first, `llama3.2:3b`), `serverless` (locked disabled until the Meta
default is fully tested). Local TTS stays disabled pending the ED-EL-05
decision; text fallback applies when voice is down. The multi-turn loop
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
FullyQualifiedName~Ellai` (unit/contract, 184 passing, 3 skipped) and
`dotnet test tests/SwiftAid.Ui.Tests --filter FullyQualifiedName~Ellai`
(~44 passing). PostgreSQL integration tests
(`tests/SwiftAid.Integration.Tests`, `EllaiPostgres*`) run only with
`SWIFTAID_RUN_SQLAUTH_FIXTURES=1` and the approved disposable loopback
connection (127.0.0.1:32703); otherwise they skip. Provider, web and
Android smoke checks are recorded separately and never mocked.
