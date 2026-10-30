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
| `ellai_state` | Minimized current feature/resource projection and permitted actions. |
| `ellai_evidence` | Bounded authorized source items and versions. |
| `ellai_topic` | Session topic candidates with checkpoints. |
| `ellai_capabilities` | Current capability descriptors and readiness. |
| `ellai_receipts` | Existing receipts with linked state. |
| `ellai_productfacts` | Versioned nonmedical product facts and help. |
| `ellai_now` | Current server time (UTC, round-trip + unix ms); no domain data. |
| `ellai_docs_search` | Search this approved reference by topic/section id. |
| `ellai_docs_get` | Retrieve one approved section excerpt with provenance. |
| `ellai_docs_search` | Search this approved reference by topic/section id. |
| `ellai_docs_get` | Retrieve one approved section excerpt with provenance. |

Compatibility aliases (accepted, metered, never advertised):
`ellai_voice_capability` → `ellai_capabilities`,
`ellai_guidance_session` → `ellai_state`. Retired names
(`ellai_caption`, `ellai_guidance_anchor_catalogue`) have no backing
projection and are rejected as unregistered until the UI-catalogue slice
lands.

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
status line binds to these next. The `EllaiUsageLedger` enforces a
200,000-token budget plus a $70 per-envelope and $70 total spend cap
(dollar caps stay inert until real Meta rates are configured).
**Deliberate deviation:** the requirements handoff asks for
3 iterations / 4 calls / 1 repair per cycle; the owner previously directed
the highest possible ceilings to avoid frustration, so the raised ceilings
stand and that handoff item is recorded as owner-deferred, not implemented.

## 4. Sessions, approvals, receipts

**Status: Implemented (sessions durable; approvals Stub).** Sessions persist
in PostgreSQL (`EllaiSessionEntity`, `EllaiCheckpointEntity`,
`PostgresEllaiSessionStore`) with version-fenced compare-and-set and
idempotent utterance/checkpoint identities; restart and second-gateway
visibility hold. Approvals carry a 15-minute expiry and idempotent durable receipts
(`EllaiApprovalService` over `IEllaiApprovalStore`, PostgreSQL
`ellai_plan_approvals` with unique idempotency-key and plan/action
constraints) with an Accepted / AcceptedUnverified / UnknownOutcome /
PartiallyApplied / Applied / Failed lattice. A server-side dialog
coordinator enforces one active review per session, edit-then-new-version
invalidation, and terminal cancel fencing; the approval endpoint binds
dialog-issued submissions to their open review (409 when stale). Still
**Missing**: the atomic per-action domain executor with outbox commit, the
Blazor dialog host, and cross-instance dialog fencing. HTTP: `POST /ellai/v1/sessions/{id}/approvals`,
`GET /receipts`, utterance submit, terminal cancel
(`src/SwiftAid.Gateway/Endpoints/EllaiEndpoints.cs`). Approval is granted
only by operating the authenticated approval dialog (Approve selected /
Edit / Reject / Cancel); text or voice consent is never approval.

## 5. Dynamic UI Intelligence and surface

**Status: Implemented (registry/planner/coordinator live; client
observation channel partial).** `EllaiUiRegistry` holds versioned semantic
descriptors; `EllaiGuidancePlanValidator`, `EllaiBonboardingCompiler` and
`EllaiGuidanceSessionCoordinator` plan, compile and activate one guidance
at a time. The single compact composer (`EllaiCompactComposer`) attaches
under the active Bonboarding card; `EllaiStatusLine` projects activity;
`EllaiPlanReview` renders the approval card; `EllaiLauncherMenu` is the one
shared launcher on every layout for guest and signed-in users; Settings/About
hosts history with per-session cost captions. Live client observation
(`ellai_ui_observe`) requires the active controller channel; an absent
client yields `Unavailable`, never a fabricated target.

## 6. Voice, reasoning routes, memory

**Status: Implemented, route-gated.** Meta ASR upload authenticates via the
`Authorization` header; realtime ASR authenticates in the WebSocket
handshake frame. Reasoning backends: `meta` (default hosted API), `ollama`
(local-first, `llama3.2:3b`), `serverless` (locked disabled until the Meta
default is fully tested). Local TTS stays disabled pending the ED-EL-05
decision; text fallback applies when voice is down. Memory: PostgreSQL is
authoritative for records and provenance; Qdrant (`EllaiMemoryIndex`) is a
private derived index of scoped references only — never secrets, audio, or
the case database.

## 7. Verification map

Run `dotnet test tests/SwiftAid.Application.Tests --filter
FullyQualifiedName~Ellai` (unit/contract, ~114 passing). PostgreSQL
integration tests (`tests/SwiftAid.Integration.Tests`, `EllaiPostgresTests`)
run only with `SWIFTAID_RUN_SQLAUTH_FIXTURES=1` and the approved disposable
loopback connection (127.0.0.1:32703); otherwise they skip. Provider, web
and Android smoke checks are recorded separately and never mocked.
