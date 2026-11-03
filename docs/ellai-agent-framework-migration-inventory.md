# Ellai Agent Framework migration inventory

Status: in progress. This inventory is source inspection, not evidence that the
migration or live-provider acceptance has passed. Baseline: `12e6d7a` on
`interactive-ellai` (3 November 2026).

## Current execution boundary

- `src/SwiftAid.Gateway/Ellai/EllaiUtteranceWorker.cs` resolves the scoped
  `IEllaiReasoningExecutor` and supplies its dependencies to `EllaiUtteranceRunner`.
  Gateway defaults to the existing `EllaiReasoningLoop` for that interface.
  `Ellai:ExecutionEngine=framework` explicitly selects the framework executor;
  keep this opt-in disabled until the cutover acceptance gates below pass.
  Set `Ellai:ExecutionEngine=legacy` or remove the setting to roll back.
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
- `EllaiExecutionPromptBuilder` now shares that trusted policy, contextual and
  guidance construction. Its framework variant instructs native function
  calling while retaining the final-decision schema and evidence rules. The
  framework executor uses this builder when the opt-in path is selected.
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

`EllaiMuseChatClientFactory` creates an official `IChatClient` through
`Microsoft.Extensions.AI.OpenAI` 10.10.1 and OpenAI SDK 2.14.0, using the
existing configured Meta endpoint, model and key. The Gateway registers it
only for the explicit framework selection. A synthetic HTTP handler verified the request path, bearer header,
model, tool declaration and native tool-call response parsing. The SDK sends
`max_completion_tokens`; the existing Meta adapter sends `max_tokens`.
Validate safe error handling, SDK logging and remaining usage and token-limit
behavior before routing production traffic through it. The OpenAI SDK here is a wire
protocol adapter, not OpenAI Swarm or an alternate agent framework.

Live synthetic probes on 3 November 2026 used the running Gateway container's
configured Meta route without printing credentials. The official client
returned ordinary text with the configured model and nonzero token usage;
streaming produced the expected text. With `max_completion_tokens=1024`,
both requests succeeded. A native function declaration with automatic tool
choice returned the requested structured tool call. The same request with
`ChatToolMode.RequireAny` returned HTTP 400. The error body was deliberately
not recorded, so the precise rejection cause is unknown; this result does not
establish that all structured schemas, forced tool choices, context sizes or
streaming tool-call deltas are supported. A framework agent also invoked a
synthetic function through the live provider and used the result in its final
answer. A separate live synthetic multi-turn probe passed a randomly generated
value from one framework function result into a second function and received
its verification; this exercised dependent model/tool turns, not a fixed script.
A separate live framework-agent probe invoked the real `ellai_now`
broker tool through the admitted framework catalogue, with a synthetic session,
one-tool allowlist and consent; the broker returned an allowed result. It did
not exercise case data or the Gateway worker. The opt-in tests are in
`EllaiLiveProbeTests`; default execution skips them.

The official NuGet feed resolved `Microsoft.Agents.AI` version `1.27.0` on
3 November 2026. The Intelligence project references this version; the
Gateway's non-native WebAssembly build succeeds with it. Microsoft documents
`ChatClientAgent` over `IChatClient`,
native streaming and serializable `AgentSession`; its documentation states that
tools are invoked without user approval by default. SwiftAid authorization and
approval cannot be delegated to that default.

## Existing tests and migration gaps

## Qdrant and Redis compatibility matrix — pre-cutover gate

This matrix is based on the current source and the checks below, not on the
requirements documents alone. No infrastructure behavior or service ownership
is changed by the framework migration.

| Area | Current integration and contract | Framework compatibility and acceptance |
| --- | --- | --- |
| Qdrant exact Ellai recall | When Ellai is enabled and `Qdrant:Endpoint` is nonempty, `Program.cs` registers `EllaiMemoryIndex` and `EllaiMemoryProjectionWorker`. `EllaiMemoryServiceCollectionExtensions` fixes the base collection to `ellai-memory`; the actual vectorless collection is `ellai-memory-exact`. `EllaiCaseResultMemory` uses `IEllaiExactReferenceIndex` only for the `case-results` reference; `ellai_topic` and `ellai_evidence` reach it through `EllaiToolBroker`. | Reuse those broker and index services. Framework tests must exercise authorized recall, changed owner/scope, revoked record access, expired and stale source versions, and index outage without leaking a candidate or bypassing the PostgreSQL recheck. Synthetic framework tests cover stale, outage and changed scope; live Gateway and PostgreSQL combinations remain **NOT VERIFIED**. |
| Qdrant vector/semantic | `EllaiMemoryIndex` contains vector upsert/search methods with caller-provided vectors and session/epoch filters, but source search found no production caller of these methods or an Ellai embedding encoder. The active exact path uses no vectors. A vector adapter round trip is not proof of deployed semantic retrieval, RAG, embeddings or semantic cache. | Do not claim or enable semantic retrieval or a second pipeline. Any future encoder remains separately gated. Test existing exact behavior through the framework; verify any future semantic path independently before use. |
| Qdrant scope and lifecycle | Exact payload contains `memory_id`, `session_id`, `scope_epoch`, `topic_id`, `source_version`, `expires_at` and `encoding=exact/1`; point IDs derive from session, epoch and ID. The index filters and rechecks identity and expiry. `EllaiCaseResultMemory` verifies PostgreSQL topic/version, actor, scope, expiry and current authorized case query; it falls back to authoritative topic resolution after a 500 ms index timeout or failure. Projection reads active PostgreSQL topics, retries failures and upserts every 30 seconds; expired/stale points are ignored rather than proven physically deleted. Qdrant is derived, not case authority. | Preserve these boundaries and 24-hour snapshot behavior. Verify stale, revoked, expiry, outage, cross-session and cross-scope cases with disposable services and actual framework execution; verify restart/reprojection and cleanup separately. Existing vector `SearchAsync` trusts the remote filter and should not be exposed as an authorized tool without independent scope and payload validation. |
| Qdrant transport and persistence | Requests send the API key in the `api-key` header. Local Compose uses a private Qdrant service and `qdrant-data` volume; plaintext is explicitly configured for Development. Configuration validation checks enabled endpoint/key and transport policy, but the Ellai registration in `Program.cs` is conditioned on a nonempty endpoint, not directly on `Qdrant:Enabled`. | Preserve current configuration semantics; verify production transport and credentials separately. Do not expose credentials, raw case narratives or arbitrary Qdrant access to the model. The registration/enable-flag distinction requires review before cutover. |
| Redis derived cache | `ServiceCollectionExtensions` registers `RedisCacheStore` as `ICacheStore` when enabled and `NotConfiguredCacheStore` otherwise. Source search found no application consumer of `ICacheStore` or `IDistributedCache` beyond the adapter, and no Ellai Redis session/context or result-cache path. The adapter uses caller keys under configured `InstanceName` (default `swiftaid:`), requires an absolute TTL up to the configured maximum, and exposes hit/miss/failure/cancelled states; remove is explicit. It does not itself construct tenant keys or establish authorization. | Preserve the existing adapter without inventing an Ellai cache or a new store. Prefix isolation tests are not proof of per-user/tenant authorization. If an Ellai cache consumer is introduced later, scope/version/invalidation and access checks require a separate design and tests. |
| Redis coordination and rate limiting | The confirmed Redis consumer outside the generic cache is Facebook web login's HMAC-address bucket (`swiftaid:auth:facebook-web:rate:v2:<digest>`), atomic INCR/EXPIRE with a bounded window; failure returns `Unavailable`. No Redis Ellai session coordination or Ellai rate limiter was found. `EllaiUtteranceQueue` has a per-process gate; the active worker instead uses the PostgreSQL outbox and lease/fencing paths. | Preserve the login limiter and PostgreSQL outbox semantics. Do not claim Redis provides Ellai session recovery, cross-instance locks or rate limits. Check framework worker restart, duplicate delivery and lease fencing against disposable PostgreSQL before cutover. |
| Redis transport and persistence | Redis configuration validates the connection and TLS except allowlisted Development hosts. Local Compose uses password authentication, loopback publication and `appendonly no` without a Redis volume. Cache misses and failures are distinct, writes require TTL, and logs report failure type rather than values. | Verify isolated cache expiry, remove and outage behavior; a Redis restart must not be presented as durable Ellai session recovery. Production transport, real multi-user cache isolation and end-to-end outage behavior remain **NOT VERIFIED**. |

**Cutover acceptance:** before switching the Gateway to framework execution,
run the framework through the actual worker and authorized case services with
Qdrant hit, miss, stale, wrong session/scope, expiry and outage; confirm
PostgreSQL remains authoritative, unauthorized cases do not appear, and no
raw payload or credentials reach the model. Run Redis failure and recovery
checks against the existing shared adapters and verify Ellai's PostgreSQL
session/outbox recovery is independent of Redis. Exercise separate users,
concurrent sessions, cancellation and restart on disposable infrastructure.
Record exact results; synthetic or adapter-only checks do not satisfy the
full live Gateway acceptance gate.

**Disposable PostgreSQL recovery evidence:** `FrameworkWorkerRehydratesOwnedConversationAfterWorkerRestart`
passed (1/1, zero skipped) against a task-owned PostGIS 17 container after 60
repository migrations. With a deterministic synthetic model, it completed a
turn, disposed the worker and its service provider, then processed a second
turn using a new worker and database scope. The model received the prior user
and assistant messages, another owner could not read the conversation, and
two usage calls persisted. The fixture removed its own rows; a database query
confirmed no `framework-recovery-` sessions remained. Command: `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~FrameworkWorkerRehydratesOwnedConversationAfterWorkerRestart
--verbosity quiet`, with `SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES=1` and the
dedicated loopback `swiftaid_framework_fixture` connection. This exercises a
worker restart within one test process; a Gateway process restart, live Muse,
serialized `AgentSession` restoration and real multi-instance behavior remain
**NOT VERIFIED**.

`GuestHttpLiveMuseRecoversConversationAfterTestHostRestart` passed alongside
the synthetic-model restart test (2/2, no skips). With a task-owned PostGIS
database and configured live Muse route, the first ASP.NET Core TestServer
accepted a guest turn through the HTTP route; after that host was disposed,
a second host used the same data-protection provider and PostgreSQL store.
The owner recovered the prior response, an unauthenticated request was denied,
and Muse answered the next turn using the word supplied before restart. The
test removed its session data and the task-owned database container was removed.
Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~GuestHttpLiveMuseRecoversConversationAfterTestHostRestart|FullyQualifiedName~GuestHttpConversationRecoversAfterTestHostRestartWithPostgres`,
with `ELLAI_LIVE=1`, `SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES=1`, the
dedicated loopback PostgreSQL connection and the configured provider settings.
The hosts ran in one test process; deployed Gateway process restart, persistent
production data-protection keys and browser behavior remain **NOT VERIFIED**.

`FrameworkWorkerRecoversPostgresConversationWhileRedisIsUnavailable` passed
alongside the normal recovery fixture (2/2, zero skips) against a disposable
PostgreSQL database and a deliberately stopped disposable Redis container.
The existing Redis adapter reported read failure before and after the worker
restart; two framework worker scopes completed turns and recovered the prior
owner-filtered conversation and PostgreSQL usage. The Redis adapter was
registered in the fixture, but Ellai has no Redis session consumer. The model
was synthetic and the worker restarted within one test process; this does not
prove a deployed Gateway restart or production Redis behavior. The opt-in test
uses `SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES=1`, the dedicated PostgreSQL
connection, and `SWIFTAID_TEST_REDIS_DOWN_CONNECTION` for the stopped loopback
endpoint.

**Disposable Qdrant framework evidence:** `FrameworkRecallUsesLiveExactIndexAndRechecksCurrentCaseAccess`
passed 1/1 against a task-owned loopback Qdrant container using a uniquely
named exact collection. The framework invoked the existing broker with a
synthetic model, retrieved an indexed case reference, omitted the case when
the fixture queue revoked it, and fell back to the topic store when the index
source version was stale. The collection was deleted by the fixture. The
topic store and case queue in this test are in-memory fixtures, so this does
not prove PostgreSQL authorization, live provider execution, real service
outage behavior, or the full Gateway cutover gate. Run with
`ELLAI_LIVE_QDRANT=1`, `QDRANT_TEST_ENDPOINT` set to the disposable loopback
endpoint, and `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~FrameworkRecallUsesLiveExactIndexAndRechecksCurrentCaseAccess
--verbosity quiet`.

`FrameworkRecallUsesPostgresAuthorityWithLiveQdrant` additionally passed 1/1
with the same disposable Qdrant setup and a separate loopback
`swiftaid_framework_fixture` PostgreSQL database after 60 migrations. It uses
the real PostgreSQL topic store, case queue, case access policy and authority
lease with synthetic records. A framework tool call recalled the indexed
authorized case; after the persisted grant expired, a second framework run
did not expose that case. The model and session execution are synthetic;
this does not exercise the Gateway worker, live Muse, or real service outages.
Run the preceding command with the filter changed to this test name and set
`SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES=1` and
`SWIFTAID_FRAMEWORK_POSTGRES_CONNECTION` to the dedicated disposable database.

The worker recovery and combined PostgreSQL/Qdrant recall fixtures were then
updated to resolve their executor through `AddEllaiReasoningExecutor` with the
framework selection and a keyed synthetic model. Both passed together (2/2,
zero skips) against isolated PostgreSQL and Qdrant after 60 migrations using
the integration test command above with filter
`FullyQualifiedName~FrameworkWorkerRecallsAuthorizedCaseFromPostgresAndQdrant|FullyQualifiedName~FrameworkWorkerRehydratesOwnedConversationAfterWorkerRestart`.
This exercises the same registration as the Gateway but not the Gateway host,
real provider, or production configuration.

`LiveMuseWorkerRecallsAuthorizedCaseFromPostgresAndQdrant` and its synthetic
model control passed 2/2, twice, against dedicated disposable PostgreSQL and
Qdrant containers. The framework worker called the existing topic tool and
attempted Qdrant exact recall on each turn. Live Muse returned the synthetic
authorized case identifier from the indexed topic, returned it again after a
simulated Qdrant transport failure with PostgreSQL fallback, and did not return
it after the persisted case grant expired. The outage was injected in the HTTP
transport, not by stopping Qdrant. These two model runs do not prove
reliability across arbitrary prompts, concurrent revocation, an actual network
outage, or the deployed Gateway HTTP path. The strengthened rerun counted
each recall request. Run with `ELLAI_LIVE=1`, `ELLAI_LIVE_QDRANT=1`,
`SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES=1`, configured Muse settings,
`QDRANT_TEST_ENDPOINT` and `SWIFTAID_FRAMEWORK_POSTGRES_CONNECTION` pointing
to dedicated loopback fixtures, and `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
'FullyQualifiedName~LiveMuseWorkerRecallsAuthorizedCaseFromPostgresAndQdrant|FullyQualifiedName~FrameworkWorkerRecallsAuthorizedCaseFromPostgresAndQdrant'`.

`GuestHttpTurnsReachFrameworkWorkerAndRetainConversation` passes through the
real guest HTTP routes in an ASP.NET Core TestServer, cookie and antiforgery
filters, the worker and the framework registration. Two submitted turns
complete, the second model request contains the earlier exchange, the response
feed returns the answer to its owner, and a request without the guest cookie is
rejected. The model and persistence are synthetic/in-memory; this is not a
production Gateway process or a real provider/device test. The enclosing
`EllaiPrivilegedRouteAuthorizationTests` suite passed 21/21 with
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-build --no-restore -m:1 -nr:false --filter
FullyQualifiedName~EllaiPrivilegedRouteAuthorizationTests --verbosity quiet`.

`GuestHttpConversationRecoversAfterTestHostRestartWithPostgres` passed against
a disposable PostgreSQL database after 60 migrations. It starts a guest session
and completes a turn through the HTTP endpoints and framework worker, disposes
the TestServer host, and starts a new host using the same database and shared
in-memory test data-protection provider. The existing guest cookie retrieves the
durable response after a stale feed cursor, a request without the cookie is
rejected, and a follow-up reaches the framework with the earlier exchange. The
full endpoint authorization fixture passed 22/22, zero skipped, with
`SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES=1` and its dedicated loopback database.
Both hosts ran in one test process with a synthetic model; actual Gateway
process restart, deployed key storage and live Muse across a restart remain
**NOT VERIFIED**.

`GuestHttpFrameworkUsesLiveMuseAndDurableMultiTurnHistory` passed 1/1 against
the configured `muse-spark-1.3-contributor` endpoint and a disposable
PostgreSQL database after 60 migrations. Through the guest HTTP routes, test
host, worker and framework registration, two synthetic turns completed; the
second answer recalled the word from the first, its response feed contained a
non-final text chunk, and a third turn invoked the admitted public-facts tool:
the feed reported its specific acknowledgement and PostgreSQL recorded at
least four model calls with nonzero usage. The first extended run failed its
progress assertion because the feed excludes progress by default; after
requesting `includeProgress=true`, the live test passed. The provider route was supplied in process without committing
credentials. Run the integration test filtered by this name with
`ELLAI_LIVE=1`, the configured Ellai environment variables, and the dedicated
PostgreSQL fixture opt-in. This was ASP.NET Core TestServer, not a deployed
Gateway process, browser, voice or managed-case provider acceptance test.

`FrameworkWorkerRecallsAuthorizedCaseFromPostgresAndQdrant` passed 1/1 using
the actual outbox worker, scoped broker, PostgreSQL session/topic stores and
case authorization, plus a disposable Qdrant exact collection. With a
synthetic streaming model and synthetic case, an initial turn observed the
authorized case; a follow-up with a simulated Qdrant transport outage fell
back to PostgreSQL and retained the case, and another after persisted grant
expiry observed an allowed empty result without the case. The second request
included the prior user and assistant messages. All three turns completed in
the same worker process; the outage was injected in the HTTP transport, not
by stopping the Qdrant service.
The first fixture attempt timed out because the synthetic processing label
was ineligible for response publication and the fixture model implemented
streaming only; the rerun uses the existing typed-turn publication contract.
This does not establish a live Gateway process, provider, outage, or full
cutover acceptance. Use both fixture opt-ins above and filter the integration
test command by this test name.

**Evidence this audit:** `EllaiLiveQueueReadTests` passed 24/24, including
four framework/broker cases for exact hit, stale reference, index outage and
wrong epoch, with a fake index and synthetic queue. The focused integration
run for memory index/projection, Redis configuration and Facebook exchange
passed 42 with 4 opt-in skips. Against an isolated task-owned Qdrant 1.19.0
container, the existing exact and vector adapter round trips passed 2/2.
Against a separate disposable Redis 8.4.7 container, instance-prefix isolation,
expiry/removal and the Facebook rate-limit fixture passed; after removing that
container, the cache adapter returned explicit failure for read/write/remove.
Both containers were removed. These tests do not establish real provider
authorization or production persistence.

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

`EllaiAgentFrameworkContractTests` uses the real framework agent and a
deterministic `IChatClient` to verify that the framework sends a function
schema, invokes the function and returns its result to the client. It passed
1/1 with `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~EllaiAgentFrameworkContractTests`.
This fixture does not invoke SwiftAid tools or establish production execution.
`EllaiFrameworkTool` adapts canonical argument schemas to framework functions,
validates string arguments and calls a server-supplied invocation delegate.
`EllaiFrameworkToolTests` cover schema parity, a real broker consent denial,
malformed arguments and cancellation before invocation. This adapter is not
registered independently in the Gateway; the opt-in executor constructs it.
The production caller must still provide admitted
tools, current-scope checks, bounded execution, denial termination, evidence
collection, progress and audit handling before it can be used safely.
`EllaiFrameworkToolCatalog` snapshots the existing server-owned allowed-tool
profile, advertises only registered canonical tools and retains the broker's
model-facing descriptions. Its invocation delegate must still reach the broker
and recheck current authorization; catalogue membership is not authorization.
`EllaiFrameworkToolGuard` provides a per-run shared call budget and serial
invocation fence, checks execution currency before and after each call, and
suppresses the result when currency is lost. Its sticky failure code must be
checked by the agent runner before accepting any final answer; the opt-in
executor constructs it through `EllaiFrameworkAgent`. It does not itself implement
model-call budgets, evidence validation, progress, timeout or audit handling.
`EllaiFrameworkAgent` now constructs a framework agent with framework-managed
function invocation and a 12-iteration limit, applies the guard, and validates
the final Ellai decision and cited tool evidence. Its deterministic tests
cover an allowed call and rejection after a broker denial. It is a building
block used by the opt-in Gateway execution path. Full end-to-end validation of
policy/context, per-model unknown-tool handling, durable sessions,
streaming, timeout and audit integration remains incomplete. It currently
creates a new framework session per invocation. The session is seeded through
the framework's in-memory chat-history provider from the existing bounded,
owner-filtered completed-turn projection, while the outbox remains the sole
durable conversation authority. A two-turn in-memory worker fixture confirms
the previous exchange reaches the next framework session in order. This is
rehydration from durable-conversation abstractions, not persistence or
restoration of the framework session's serialized state. A disposable
PostgreSQL worker-scope recovery test is described below; full process-restart
recovery remains unverified.
`EllaiFrameworkExecutor` implements the application execution boundary using
the shared framework prompt, filtered conversation, tool guard and usage store.
An in-process fixture verifies a successful tool call and activity event. It
is available through the explicit Gateway selection: durable framework sessions
and end-to-end Gateway acceptance still require verification. The executor emits pending-tool events,
maps known authorization and budget denials to safe blocked codes, and
sanitizes model-provider exceptions while preserving worker retry behavior.
Focused tests cover denial and synthetic provider failures. Framework tool
execution now requests cancellation after 30 seconds and rejects results
that arrive after that deadline as unavailable. This new bound addresses the
migration's timeout acceptance requirement; the prior loop had no general
tool deadline. It is cooperative: an implementation that ignores cancellation
can still hold the run and its service scope open, although its late result is
not accepted. Focused synthetic tests cover cooperative cancellation, deadline
expiry and a late successful result. Live outage and long-running real tool
behavior remain unverified.
Focused framework tests also exercise both synchronously thrown and faulted
tool tasks containing a synthetic secret. Both return an unavailable outcome
without a decision, and the synthetic secret appears neither in the outcome nor
in tool results observed by the test model. The framework contract suite passed
33/33. This verifies synthetic exceptions, not a real service outage or a
non-cooperative tool deadline.
An in-memory worker integration fixture now runs this executor with a
deterministic streaming `IChatClient`, checks the owner-filtered completed
turn, response chunk, final reconciliation and usage sample. It does not test
PostgreSQL persistence, a live Gateway process, or a serialized framework
session. A second deterministic worker case invokes the real scoped
`ellai_now` broker and verifies its observation and progress acknowledgement.
The framework model-call guard rejects named tool calls outside the advertised
catalogue; malformed partial streaming call handling still requires additional
provider-level verification.
Deterministic framework tests also cover two dependent tool calls with exact
evidence validation and concurrent session history isolation. Invalid final
decisions now get at most two model-only framework correction runs in the same
session, with tool definitions suppressed and one shared model-call limit.
Tests cover repair success, exhaustion and a rejected streamed draft without
appending repaired text. Live Muse correction behavior, full process recovery,
and complete multi-turn parity remain cutover acceptance requirements.
Its non-streaming provider calls recheck the supplied currency callback and
use `EllaiFrameworkUsage` to estimate the prompt and tool-schema budget against
the existing ledger, then record provider usage through `IEllaiUsageStore`.
The estimate and recording are covered by an in-memory fixture, not a live
PostgreSQL process restart or multi-instance concurrency test. The existing ledger's
`TryReserve` is a check, not an atomic cross-instance reservation. The
isolated runner now uses framework streaming with the existing decision-text
parser and checks currency and budget before each streamed provider call. It
records usage from provider usage updates, including usage received before an
interruption. If a provider ends or is interrupted without sending usage,
actual billable usage cannot be established from that stream. A deterministic
text-stream fixture passes. A streamed tool fixture exposed that intermediate
assistant text could be concatenated with the final decision; the runner now
validates only the last model call and resets its text parser between calls.
The live Muse probe then passed with the brokered `ellai_now` tool, a validated
decision, streamed display text and nonzero recorded usage. This is a synthetic
isolated runner, not Gateway evidence. Gateway correlation, interruption,
voice delivery and managed-case access remain unverified.
The deterministic framework fixture also invokes the real broker's admitted
public-facts tool and observes its serialized JSON evidence. This is not a live
provider or managed-case test. The framework and adapter focused tests passed
5/5 after an initial failure exposed that the tool result needed explicit JSON
serialization to preserve evidence in the model-facing observation.
`dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false` passed. The default build failed at native WebAssembly linking because `emcc` was unavailable (exit 9009).

## Rollback and outstanding work

`RecordedVoiceFlowsThroughFrameworkWorkerToPreparedSpeech` exercises recorded
voice admission, the framework worker, response streaming and prepared PCM
with synthetic ASR, model and speech providers and an in-memory session store.
The transcript reaches the model and the prepared audio contains the fixture
PCM. The first run failed because the fake speech provider did not implement
the selected `af_heart` profile; the corrected test passed. The enclosing
voice transcription suite passed 18/18 using `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build
--no-restore -m:1 -nr:false --filter
FullyQualifiedName~EllaiVoiceTranscriptionServiceTests --verbosity quiet`.
This is not live transcription, actual Kokoro inference, browser playback,
device capture or a deployed Gateway check.

`RecordedVoiceFrameworkResponseUsesLiveSpeech` passed 1/1 against the local
`swiftaid/ellai-tts:kokoro-mms-local` service through a task-owned loopback
proxy. The framework worker's synthetic response was synthesized with the
`af_heart` profile and returned nonzero PCM; ASR, model and session storage
were synthetic/in-memory. The companion synthetic test also passed (2/2
together). Use `SWIFTAID_TEST_LIVE_SPEECH=1`, the loopback speech endpoint and
its internal key with the integration test filter for this test. Credentials
were supplied only in process. This does not verify live transcription,
audio quality or browser/device playback.

`RecordedVoiceUsesLiveMuseAndSpeech` passed 1/1 with the configured
`muse-spark-1.3-contributor` route and local Kokoro speech service together.
A synthetic recorded-audio fixture and synthetic ASR result were admitted to
the framework worker; its live model response produced nonzero `af_heart` PCM.
Session storage was in memory and speech was accessed through a task-owned
loopback proxy. Run the integration test filtered by this name with both
`ELLAI_LIVE=1` and `SWIFTAID_TEST_LIVE_SPEECH=1`, the provider settings and
the loopback speech fixture. This is not live microphone capture, real ASR,
browser/device playback or a deployed Gateway process test.

`SyntheticSpeechFlowsThroughLiveTranscriptionMuseAndSpeech` passed 1/1 with
synthetic speech generated by the local Kokoro service, the configured live
transcription adapter, the framework worker and the configured Muse model,
followed by local Kokoro output. The transcript contained the synthetic
greeting and the resulting speech contained nonzero PCM. Session storage was
in memory; the speech service was accessed through a temporary loopback proxy.
The opt-in run uses `ELLAI_LIVE=1`, `SWIFTAID_TEST_LIVE_ASR=1`,
`SWIFTAID_TEST_LIVE_SPEECH=1`, provider settings and the loopback speech
fixture with `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~SyntheticSpeechFlowsThroughLiveTranscriptionMuseAndSpeech`.
The ordinary voice suite passed 18 tests with three live tests skipped.
Microphone capture, browser/device playback, durable session storage and the
deployed Gateway route remain **NOT VERIFIED** by this test. A first build
without `WasmBuildNative=false` failed because the native `emcc` linker was
unavailable; it did not execute the test.

The analytics acceptance requirement previously had no complete aggregate
tool: `ellai_state` returns a bounded case page, which is not a total. The new
`ellai_owned_case_count` adapts the existing `IDashboardAggregateQuery` for a
required 1–365 day lookback. It returns only a count of managed records owned
by the current actor and authorized by that query, labels the creation-time
lower bound and query time, and returns unavailable for denied, failed or
truncated results. It is absent from the guest allowlist; it is not an
incidence, diagnosis or animal count. Framework execution against disposable
PostgreSQL passed 1/1: the count matched the authorized query and fell to zero
after grant expiry without exposing case IDs. Application tests passed
996/1006, with 10 opt-in skips. The first full run failed two catalogue/document
sync tests, corrected before the passing rerun. An opt-in live
`muse-spark-1.3-contributor` framework test invoked this query against synthetic
PostgreSQL cases and passed with the returned count and exact evidence cited.
After strengthening the assertion to require the count as a standalone number
in both display text and cited claim, one quiet run failed without preserving
the assertion detail and a subsequent run passed. This mixed result does not
establish reliable live analytics; Gateway end-to-end analytics remains
**NOT VERIFIED**. The live run uses `ELLAI_LIVE=1`, configured Ellai provider
environment variables, the dedicated PostgreSQL fixture opt-in and
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~LiveMuseUsesAuthorizedPostgresCountAndCitesObservedEvidence`.

**Analytics safeguard and remaining gap:** when the count tool is available,
the framework buffers model text. After one successful count call, the final
display and spoken text come directly from the authorized tool summary and
carry its evidence; mixed tool runs involving a count fail closed. A synthetic
streaming test verifies that a model-invented number is not published. This
delays streaming for sessions where the count tool is available, and mixed
analytics requests are not yet supported. The safeguard does not prove that a
model will call the count tool for every analytics question or prevent an
unsupported count when no such call occurs. Live Gateway, voice, revoked-access
and multi-tool behavior remain cutover acceptance requirements.
The focused `CountDoesNotPublishWhenAuthorizationExpiresDuringToolCall` test
also verifies that a changed execution-currency callback blocks streaming and
returns a policy denial. It does not prove that a database grant revocation
changes that callback during an in-flight count. The framework contract suite
passed 25/25; the initial test expectation was corrected to match the agent's
safe denial exception.
The framework now repeats the same authorized count read immediately before
publication and withholds the answer if the count changes, the recheck fails,
or execution is no longer current. This is an additional server-side read,
not a model-visible tool invocation. A disposable PostgreSQL test expired the
actual grant after the first read and verified the answer was withheld; it and
the ordinary authorization fixture passed 2/2 after 60 migrations. The check
and publication are not atomic, and same-count changes are not detected; a
grant can still change after the recheck. The model may already have received
the earlier authorized observation. Live Gateway and voice behavior remain
**NOT VERIFIED**.
The executor routes this recheck through the same timeout and scoped broker
without counting it as another model tool call or publishing a second tool
start/observation event. A focused framework test verifies two broker reads,
one model-visible call and one set of progress events; the framework contract
suite passed 27/27 and the application suite passed 1001 with 10 skips.
Additional executor tests verify that a cooperative timeout during the second
read returns unavailable without publishing and that caller cancellation
propagates without publishing. They also check the model-visible tool count
and progress remain unchanged. The framework contract suite passed 29/29 via
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj
--no-restore -m:1 -nr:false --filter FullyQualifiedName~EllaiAgentFrameworkContractTests
--verbosity quiet`. Non-cooperative dependencies can still hold their scope
open, and this synthetic check is not a live service-outage test.

The framework now checks the execution fence immediately before final
publication and again before returning a successful decision. If the fence
changes, it returns a policy denial instead of reporting completion. Focused
tests cover a fence becoming stale after count revalidation but before the
callback, and becoming stale during a callback. The latter test confirms that
an already delivered chunk cannot be retracted; this is not an atomic
authorization-and-publication guarantee. The framework contract suite passed
31/31, and the guest-route and voice integration suites passed 39 tests with
six opt-in tests skipped. These are synthetic fence transitions, not a live
distributed grant-revocation test.

An opt-in Gateway execution selection has been added; its default remains the
existing loop. Invalid selection and unconfigured or unsupported framework
provider routes fail closed. The registration and worker fixtures passed 6/6
using `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
'FullyQualifiedName~EllaiReasoningRegistrationTests|FullyQualifiedName~WorkerRunsFrameworkExecutorAndPersistsAndStreamsAnswer'
--verbosity quiet`. The worker test uses a synthetic model and in-memory stores,
not a running Gateway. Keep the current execution path available until the framework path and its safety,
persistence and client acceptance tests pass. No obsolete orchestration has
been removed. Production framework-controlled execution, provider
compatibility, acceptance tests and deployment are **NOT VERIFIED**.
