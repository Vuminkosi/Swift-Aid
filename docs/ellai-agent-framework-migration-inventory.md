# Ellai Agent Framework migration inventory

Status: in progress. This inventory is source inspection, not evidence that the
migration or live-provider acceptance has passed. Baseline: `12e6d7a` on
`interactive-ellai` (3 November 2026).

## Current execution boundary

- `src/SwiftAid.Gateway/Ellai/EllaiUtteranceWorker.cs` resolves the scoped
  `EllaiReasoningLoop` and supplies its dependencies to `EllaiUtteranceRunner`.
  The worker claims and completes outbox work, checks the session, hydrates
  consent and usage, and publishes correlated response chunks.
- `src/SwiftAid.Application/Ellai/EllaiUtteranceRunner.cs` obtains the
  owner-filtered conversation, constructs the tool context, handles translation,
  and provides authorization and execution-current callbacks to the loop.
- `src/SwiftAid.Application/Ellai/EllaiReasoningLoop.cs` builds policy and
  contextual instructions, uses a custom JSON turn protocol, records provider
  usage, validates final decisions and checks claim references against observed
  tool evidence. Its custom model/tool iteration must be replaced, while these
  safety and output contracts require equivalent enforcement.
- `src/SwiftAid.Application/Ellai/EllaiCycleRunner.cs` controls iterations,
  tool-call and repair limits, reauthorization, cancellation, activity and
  tool observations. Framework execution must retain the relevant limits,
  scope fences, progress and failure semantics without retaining a competing
  custom model/tool orchestration loop.
- `src/SwiftAid.Application/Ellai/Tools/EllaiToolBroker.cs` is the existing
  tool authorization and execution boundary. The model must receive only
  admitted schemas; every invocation must still pass server-side checks.
  `EllaiToolContractCatalog.cs` holds the existing tool contracts.
- `src/SwiftAid.Intelligence/Meta/EllaiTurnClientAdapter.cs` adapts
  `IEllaiTurnCompletionClient` to `EllaiReasoningRouter`. The interface also
  serves `EllaiModelGuidancePlanner`, so removal requires checking that
  separate consumer, not just the primary utterance path.
- `src/SwiftAid.Application/Ellai/EllaiSessionService.cs`, the utterance
  outbox and conversation context are existing durable session boundaries.
  Agent session serialization must be coordinated with the fenced durable
  turn commit; an in-memory framework session alone does not establish
  restart recovery, isolation or exactly-once behavior.

## Provider observations and open checks

`EllaiReasoningRouter` selects Meta, Ollama or an explicitly enabled serverless
route. The serverless adapter posts to `/chat/completions` with a bearer token,
configured model and `max_tokens`. It serializes prior tool call messages, but
the inspected request does not supply tool definitions and its response parser
reads text rather than native tool calls. Its default streaming implementation
falls back to a completed response. This is a source observation, not proof of
what Muse Spark or a particular host supports.

The Meta adapter and configured endpoint, model, authentication, actual native
tool calls, streaming, usage, context limits and error behavior still need
provider-specific investigation and live compatibility evidence. Do not copy
credentials into this document or tests. The supplied guide's Baseten model
alias is host-specific (`implementation.md` section 14.9).

Microsoft's `Microsoft.Agents.AI` NuGet listing reported version `1.27.0`
on 3 November 2026. This is a candidate, not an installed or compatibility-
tested dependency. Microsoft documents `ChatClientAgent` over `IChatClient`,
native streaming and serializable `AgentSession`; its documentation states that
tools are invoked without user approval by default. SwiftAid authorization and
approval cannot be delegated to that default.

## Existing tests and migration gaps

Relevant existing suites include `EllaiReasoningLoopTests`,
`EllaiCycleRunnerTests`, `EllaiBrokerBoundsTests`, `EllaiPolicyGuardrailTests`,
`EllaiUsageDurabilityTests`, `EllaiUtteranceRunnerTests`,
`EllaiUtteranceWorkerOutcomeTests`, and `EllaiPostgresTests`. Their presence
does not establish current pass status or framework coverage.

Add focused tests that exercise a real framework agent with a deterministic
`IChatClient`: native tool schema and invocation, denied and unknown calls,
revocation between calls, evidence validation, usage and limits, cancellation,
streaming correlation and concurrent session isolation. Exercise durable
session restoration and stale-claim fencing across service restarts with a
disposable PostgreSQL fixture. Live Muse and end-to-end text and voice tests
remain separate acceptance gates. Verify actual case results and aggregations
against authorized synthetic records, not mock prose.

## Rollback and outstanding work

No runtime or schema changes have been made in this inventory slice. Keep the
current execution path available until the framework path and its safety,
persistence and client acceptance tests pass. No obsolete orchestration has
been removed. Framework-controlled execution, package restore, provider
compatibility, acceptance tests and deployment are **NOT VERIFIED**.
