# Ellai Agent Framework migration inventory

Status: in progress. The uncommitted framework path has source, synthetic,
disposable-service and limited live-provider evidence documented below; the
complete migration and deployed cutover are not accepted. Baseline: `12e6d7a`
on `interactive-ellai` (3 November 2026).

## Current execution boundary

- `src/SwiftAid.Gateway/Ellai/EllaiUtteranceWorker.cs` resolves the scoped
  `IEllaiReasoningExecutor` and supplies its dependencies to `EllaiUtteranceRunner`.
  The uncommitted source now defaults to `EllaiFrameworkExecutor` when no
  execution engine is specified; `Ellai:ExecutionEngine=framework` also
  selects it explicitly. The Gateway no longer registers a legacy executor;
  `legacy` and other unsupported values fail at startup. Per the current user
  instruction, rollback to the legacy executor is no longer a migration goal.
  This source change has not been deployed or accepted: the cutover acceptance
  gates below remain open. Earlier rollback observations in this inventory are
  historical evidence, not instructions for the current implementation.
  The worker claims and completes outbox work, checks the session, hydrates
  consent and usage, and publishes correlated response chunks.
- `src/SwiftAid.Application/Ellai/EllaiUtteranceRunner.cs` obtains the
  owner-filtered conversation, constructs the tool context, handles translation,
  and provides authorization and execution-current callbacks to the executor.
- The former production `EllaiReasoningLoop` and `EllaiCycleRunner` drove custom
  JSON model/tool iteration. They are removed from production source and kept
  only under `tests/Shared/EllaiLegacy` for historical regression tests.
  `EllaiExecutionLimits`, `EllaiReasoningContracts` and
  `EllaiExecutionProvenance` hold the shared limits and contracts; the framework
  executor enforces the corresponding safety and output boundaries.
- `EllaiExecutionPromptBuilder` supplies the trusted policy, contextual and
  guidance instructions, native function protocol, final-decision schema and
  evidence rules to the framework executor.
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
  Each framework run creates an `AgentSession` from the reauthorized durable
  conversation; framework session state itself is not serialized. The
  PostgreSQL and test-host recovery checks below cover that adapter's tested
  cases, not exactly-once provider execution or every restart scenario.

History reconstruction now also excludes a prior tool-derived answer when the
current session allowlist no longer admits that tool, even if the underlying
case or aggregate remains readable. Synthetic aggregate tests cover retained
and removed admission; the focused authorization suite passed 46/46 and the
application suite passed 1,113 with 11 opt-in skips. This does not replace an
independent security review or a deployed permission-change test.
The disposable PostgreSQL worker test subsequently passed after three synthetic
turns across worker restarts: previously allowed clock-derived history appeared
on turn two, then both it and the derived answer were excluded when turn three's
session allowlist removed the clock tool. A separate recovery control also
passed; the disposable container was stopped.
An additional runner-level synthetic case test confirms that removing topic
tool admission excludes the prior case answer before the case queue is queried;
the focused history-authorization suite passed 47/47.

## Provider observations and open checks

`EllaiReasoningRouter` selects Meta, Ollama or an explicitly enabled serverless
route. The serverless adapter posts to `/chat/completions` with a bearer token,
configured model and `max_tokens`. It serializes prior tool call messages, but
the inspected request does not supply tool definitions and its response parser
reads text rather than native tool calls. Its default streaming implementation
falls back to a completed response. This is a source observation, not proof of
what Muse Spark or a particular host supports.

**Provider compatibility:** the framework Gateway registration now accepts the
configured Ollama route as well as the default Meta/Muse route. It uses the
same configured server and model without sending credentials, but changes
the wire endpoint from `/api/chat` to `/v1/chat/completions` so the official
chat adapter can supply framework-native function calls. This is a deliberate
protocol change: [Ollama's compatibility documentation](https://docs.ollama.com/api/openai-compatibility)
documents the endpoint, and its [tool-calling documentation](https://docs.ollama.com/capabilities/tool-calling)
describes tool-capable models. Synthetic tests cover the request, streaming and
framework tool invocation; they do not establish compatibility of the installed
server version or configured model. A check of `127.0.0.1:11434/api/tags`
found no local service running. The explicitly enabled
serverless route now uses the official chat-completions adapter inside the
framework executor and preserves the previous completed-response streaming
fallback. Synthetic HTTP tests cover its endpoint, bearer credential, model,
native tool invocation and final decision; Gateway registration tests passed
5/5 and provider adapter tests passed 7/7. The serverless transport converts
the SDK's `max_completion_tokens` to the previous route's `max_tokens`, with
a synthetic HTTP contract test. The full application suite passed 1,105 with
11 opt-in skips. The previous adapter does not establish that any particular
serverless provider supports native tool calls. No live serverless provider or
Ollama framework execution has been verified; provider parity remains open.
The provider adapter suite passed 11/11, and Gateway registration passed 6/6.
The synthetic Ollama-compatible and serverless framework runs also verify that
the follow-up request includes the tool result with its original call ID. No
model manifests were present in the configured local Ollama model directory,
so no local model was downloaded or live execution claimed.
Both restored routes were also tested with a provider response requesting a
tool absent from the session allowlist: the framework rejected it before the
tool delegate ran. The focused provider suite passed 13/13. Gateway startup
registration uses the selected route's own configuration checks; this is not
a deployed Gateway or independent authorization review.

Gateway usage accounting now applies the already-configured Ellai spending cap
and input, output and audio prices to the ledger. Previously the Gateway
constructed the default unpriced ledger even when these settings were present;
the change makes the configured budget effective during framework execution.
The existing per-envelope ceiling remains at most $70, the configured cap is
the total ceiling, and negative cap or price values fail at startup. A focused
registration suite passed 8/8, including concurrent in-process reservation
and invalid configuration. Cross-process reservations are not coordinated by
this ledger, so these tests do not establish a strict distributed spending cap.
The suite subsequently passed 9/9 with an additional DI-resolved framework
executor test: an exhausted configured cap denied the run before the synthetic
provider was called. This does not establish behavior across Gateway processes.
Two disposable PostgreSQL worker-restart tests subsequently passed with the
configured price table: conversation history and the exact synthetic reported
spend survived worker recreation, including when the loopback Redis endpoint
was unavailable. The temporary database container was stopped. These checks
do not establish live provider or multi-process reservation behavior.
Two further synthetic framework-provider tests cover serverless HTTP 401
sanitization and cancellation reaching the HTTP transport without publishing an
answer; the focused provider suite passed 9/9. These are fixture results, not
evidence of a live provider outage or live cancellation.

The live and synthetic provider checks below cover the configured Meta route,
ordinary text, native tools and streaming. Context limits, all failure modes
and the split-name behavior of live Muse remain open. Do not copy credentials
into this document or tests. The supplied guide's Baseten model alias is
host-specific (`implementation.md` section 14.9).

`EllaiMuseChatClientFactory` creates an official `IChatClient` through
`Microsoft.Extensions.AI.OpenAI` 10.10.1 and OpenAI SDK 2.14.0, using the
existing configured Meta endpoint, model and key. The Gateway registers it
for the default and explicit framework selection. A synthetic HTTP handler verified the request path, bearer header,
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

The following tracks the ten requested demonstrations; evidence listed here is
not a production cutover approval.

**Cutover acceptance: 0/10 closed.** All ten rows have implementation or test
evidence, but each retains the stated open condition. This count is for the
pre-cutover acceptance gate, not a claim that no implementation is complete.

An isolated freshly published Gateway, disposable PostgreSQL and live Muse were
used to cancel a synthetic guest turn after the worker entered processing.
`node tests/SwiftAid.Ui.Tests/ellai-cancel.gateway.browser.cjs` passed with the
live-provider opt-in: the terminal outcome was `Cancelled`, no answer or answer
chunk was published, and a separate database count confirmed one model usage
record. The browser test was committed as `25de7b1`; the Gateway and disposable
database were stopped. This does not establish immediate remote cancellation,
remote billing cessation, a deployed Gateway, or physical-device behavior.

Live Muse streaming cancellation was exercised through the framework agent:
after an actual provider update, a test wrapper cancelled the caller, no answer
was published, and one model dispatch was recorded. With `ELLAI_LIVE=1` and
the existing provider configuration, `dotnet test
tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore
-m:1 -nr:false --filter FullyQualifiedName~LiveMuseStreamingCancellationStopsFrameworkTurn
--verbosity quiet` passed 1/1. This is a live provider/agent check with
in-memory usage, not an actual Gateway cancellation or proof the remote service
stopped billing immediately.

A synthetic streaming provider disposal failure previously escaped the
framework boundary with its internal exception text. Stream disposal is now
sanitized as provider unavailable, or as cancellation when the caller has
cancelled. Tests cover both cases; the focused framework and usage suites passed
82/82 and the live Muse streaming/brokered-tool probe passed 1/1. This does not
establish a real provider disposal failure or independent security review.

When a provider throws a network error after caller cancellation, both
non-streaming and streaming framework guards now preserve cancellation rather
than classify the turn as a provider outage; the dispatched usage estimate is
still recorded. The new synthetic tests failed before the change and passed
after it. The focused framework and usage suites passed 80/80. This does not
prove cancellation of a live provider or a hard stop of an uncooperative one.
The normal live Muse framework streaming and brokered-tool probe also passed
1/1 after this change; it did not inject a live cancellation or network outage.

The framework streaming guard now checks caller cancellation immediately after
each provider iterator step and at end-of-stream. A synthetic provider that
ignored cancellation previously produced an unregistered-tool or provider error
after cancellation; both late-update and late-end cases now throw cancellation
without publishing text. The focused framework and usage suites passed 78/78,
and the live Muse framework streaming/brokered-tool probe passed 1/1. The guard
still waits for an uncooperative provider iterator to return; this is not a hard
timeout or user-visible model-time streaming proof.

The current worktree was republished with native web assets and run as an
isolated local Gateway against a fresh disposable PostgreSQL database and live
Muse. The two-turn Chromium conversation passed: it restored the same guest
session after reload and recalled the synthetic word in the follow-up, with no
page errors. The first run failed because the test's asynchronous network
observer did not capture the created session ID even though the first turn
completed; the harness now reads the persisted browser session and compares the
network ID when available. The rerun passed and this harness fix was committed
as `f940985`. The Gateway and database were stopped. Native publish reported
SQLite WASM0001 varargs warnings; this is not deployed or physical-device
acceptance. The new publish remains in the previously retained fixture directory.

Malformed tool-call recovery was exercised through the framework function
client: an unknown argument was rejected before broker invocation, the
model-visible error omitted the synthetic argument value, and a corrected
call reached the broker once. The full framework contract suite passed 68/68.
This scripted model case does not establish all live provider error behavior.
An additional scripted framework test supplied a malformed tool call followed
by an observed claim citing invented evidence. The call never reached the
broker; evidence validation rejected the decision, and a model-only repair
returned a different validated answer. The framework contract suite passed
69/69. This proves the explicit evidence-reference check for this scenario;
it does not prove detection of unsupported assertions omitted from `claims`.

The PostgreSQL execution-history read now resolves the owner-scoped current
turn and its preceding window in one query snapshot. Disposable PostgreSQL
anchored-history, worker restart and provider-retry recovery tests passed 3/3
after this change. The database was stopped. Independent review and deployed
behavior remain unverified.
An EF command interceptor in the disposable PostgreSQL anchored-history test
confirmed that execution issues one database reader statement; the test also
passed the existing foreign-owner, cancellation, and framework-worker context
assertions. The focused PostgreSQL test passed 1/1 and the database was stopped.

If an outbox implementation returns a history window without the accepted
current turn, the runner now stops with `HistoryUnavailable` before model
dispatch instead of silently answering without context. A different session's
colliding turn ID cannot satisfy the check. Focused application history,
runner and framework tests passed 132/132; worker and privileged-route tests
passed 46 with 3 opt-in skips. This is not independent security review.

The framework tool catalogue was checked against all registered canonical
schemas: the contract uses string-valued arguments, and an unknown argument
is rejected before broker invocation for every tool. The focused adapter,
guard and framework suites passed 76/76; the added catalogue test passed again
after commit `bce6b49`. This does not replace live-provider protocol coverage
or an independent authorization review.

The utterance runner checks cancellation immediately after both input and output
translation, so a translation client returning after cancellation cannot start
reasoning or return the translated answer. The focused runner and framework
contract suites passed 82/82, including synthetic noncooperative translation
returns. This fence does not forcibly stop a translation service that ignores
cancellation while it is still running.
The same input/output cancellation cases were then exercised through the real
framework executor with a synthetic model. The first test fixture was denied by
the consent gate before translation; after granting the fixture's existing
consent, the runner and framework suites passed 84/84. Live provider
cancellation and hard termination of noncooperative translation remain unverified.

Streaming compatibility remains incomplete. The previous loop at repository
HEAD passed sentences from `EllaiDecisionTextStreamParser.Append` to its text
callback before parsing and validating the complete decision and claim evidence;
repair could therefore follow already emitted text. The current
`EllaiFrameworkAgent` streams provider updates internally, validates the complete
decision and exact observed evidence, and the worker publishes the resulting
chunks after durable completion. This preserves the evidence boundary required
by `spec.md` SA-VIS-04 but does not preserve model-time answer or audio latency.
No safe incremental decision/evidence protocol has yet been established; this
remains an implementation and acceptance gap rather than a passed streaming gate.

Latest full-suite rerun on this worktree: `dotnet test
tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore
-m:1 -nr:false --verbosity quiet` passed 1,119 with 11 skipped. `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --verbosity quiet` passed 732, skipped
302 and failed the same four service-graph tests identified below. Skips are
not passes; the full integration regression gate remains open.
After the anchored-history changes, the native Gateway build also succeeded:
`$env:OS='Windows_NT'; dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj
--no-restore -m:1 -nr:false --verbosity quiet` reported zero errors and two
WASM0001 warnings about SQLite native varargs. This build is not a deployed or
browser-runtime verification.

Framework execution now reads the existing owner-scoped history window ending at
the accepted utterance, rather than allowing newer queued turns to displace its
anchor. The client-facing recent-history query retains its existing 100-turn
contract. A disposable PostgreSQL regression with 105 later queued turns
confirmed the current turn and its authorized prior context remain available,
and foreign-owner and missing-turn reads return no history. The focused
PostgreSQL anchored-history and worker recovery tests passed 2/2; focused
application history/framework/runner tests passed 127/127. This does not
establish an independent security review or deployed behavior.
The anchored-history regression was subsequently extended through the actual
framework worker and scripted model: both prior messages reached the model even
with 105 later queued turns. The PostgreSQL anchored-history and worker restart
checks passed 2/2. Test wrappers now forward the execution-specific read; worker
and privileged-route suites passed 46 with 3 opt-in skips. The database was
disposable and was stopped. Live-provider behavior under this queue depth remains
unverified.

The framework prompt no longer tells the model that advisory session tokens
are "left" when the threshold has been exceeded. Enforced-ledger callers
retain their previous prompt context. The prompt, usage and framework contract
filter passed **119/119** with `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~EllaiReasoningLoopTests|FullyQualifiedName~EllaiUsageLedgerTests|FullyQualifiedName~EllaiAgentFrameworkContractTests' --logger 'console;verbosity=minimal'`.
The in-memory execution-history store now has matching tests for the anchored
window, foreign owner, missing turn and caller cancellation; the focused outbox
and history tests passed 54/54. The disposable PostgreSQL anchored worker test
also passed 1/1 after adding the cancellation assertion. Only the two production
stores implement this read directly; test wrappers forward it to their inner
store. The disposable PostgreSQL container was stopped.
Live Muse also passed the PostgreSQL anchored-history scenario with 105 later
queued turns, returning the synthetic word from prior context. The scripted and
live variants passed 2/2 with the disposable database and live provider enabled.
The existing non-guest session default remains 12 cycles, and model history
selection remains limited to six completed turns and 12,000 characters; these
pre-existing session/context limits are distinct from the removed fixed framework
tool-call and iteration ceilings. They remain relevant to long conversations.

NATS/JetStream compatibility: source search found no direct NATS calls in the
Ellai application, Gateway Ellai, or Meta framework directories; accepted
Ellai utterances use the existing PostgreSQL outbox. The separate ingestion
outbox publisher still uses JetStream. On this worktree its disposable PostGIS
and JetStream recovery suite passed 6/6, including replay after a simulated
crash after PubAck. Command: `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~IngestionOutboxPostgresTests --verbosity quiet`, with the
disposable intake database and loopback NATS fixture variables enabled. This is
not an end-to-end Ellai-to-ingestion test; both disposable services were stopped.

The focused service-graph rerun on the current worktree passed 17 and failed 4:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
'FullyQualifiedName~CaseCommandProcessorDependencyInjectionTests|FullyQualifiedName~ProtectedPayloadKeyRingSecurityTests'
--verbosity quiet`. The failing registration installs the community-session
reminder sender with the command pipeline, while the `UserManager<SwiftAidUser>`
it requires is registered in the separately enabled identity lane. A focused
failure confirms the missing `UserManager` during provider validation. The
registration, sender and affected test files match the active HEAD; this is
source comparison, not a baseline test run. No unrelated infrastructure behavior
was changed, and the full regression gate remains open.

After the provider-route and usage-registration changes, the full integration
suite was rerun with `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --verbosity quiet`: 731 passed, 297
skipped and the same four service-graph tests failed. This remains a failed
regression gate; skipped fixtures are not counted as passes.

The adjacent translation and classification registry checks were rerun against
a disposable loopback PostGIS database using `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
'FullyQualifiedName~EllaiLanguageClientTests|FullyQualifiedName~ClassificationRegistryTests|FullyQualifiedName~TranslationRegistryTests'
--verbosity quiet` with `SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES=1`: 18 passed,
one live language-provider test skipped. The first attempt used a database name
rejected by the fixture safeguard and failed before database access; the rerun
used the required `swiftaid_intake_test` database. These tests cover existing
registry and client contracts, including an authorized Ellai classification
read, but do not demonstrate live NLLB or BART inference through the framework.
The disposable database container was stopped.
The disposable PostgreSQL/Qdrant framework-worker recall test was extended to
start a follow-up with a healthy index, then inject a Qdrant transport failure
after the scripted framework model requested the recall tool. The worker used
the existing PostgreSQL fallback to return the authorized synthetic case; a
later revoked-grant turn omitted it. With the disposable framework PostgreSQL
and Qdrant fixture variables enabled, `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~FrameworkWorkerRecallsAuthorizedCaseFromPostgresAndQdrant
--verbosity quiet` passed 1/1. The injected outage is at the HTTP transport,
not a stopped Qdrant process or live model; both disposable services were stopped.
Provider retry recovery was also exercised with the PostgreSQL outbox: a
synthetic provider failed the first dispatch, the first worker was stopped after
the retryable outcome, and a new worker completed the same turn on attempt two.
With the disposable framework PostgreSQL fixture enabled, `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~FrameworkWorkerRecoversProviderOutageAfterWorkerRestartWithPostgres
--verbosity quiet` passed 1/1. Both workers ran in one process with a synthetic
provider; this does not establish a Gateway process restart, a real provider
outage, or exactly-once provider billing. The disposable database was stopped.
The recovery check was strengthened and rerun: the new worker used a distinct
dependency container and usage ledger, restored usage from PostgreSQL, and
matched its ledger total to both persisted dispatch samples. The same focused
command passed 1/1. The synthetic provider and both containers remained in
one process; this is still not evidence of an operating-system process restart.
The utterance runner no longer supplies the same session/history authorization
function twice to each framework execution fence; the remaining current-run
callback performs the identical checks at those boundaries. Focused application
history, framework and runner tests passed 127/127. The disposable PostgreSQL
and Qdrant worker recall/revocation and provider-recovery tests passed 2/2 after
the change. These checks do not replace an independent security review.
The live Muse variant of the framework-worker PostgreSQL/Qdrant recall test
also passed 1/1 after the fence change. It exercised the synthetic authorized
case, a follow-up while Qdrant transport was unavailable, and a later revoked
grant. Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~LiveMuseWorkerRecallsAuthorizedCaseFromPostgresAndQdrant
--verbosity quiet`, with the live provider and disposable fixture opt-ins set.
The disposable stores were stopped. This is a worker fixture, not the deployed
Gateway or an independent authorization review.
The previously skipped live language-client test was then run separately with
`--filter FullyQualifiedName~EllaiLanguageClientTests.RealPinnedProviderRespondsThroughGatewayClient`
and the existing local pinned language service reached through a disposable
loopback relay: 1 passed, 0 skipped. It sent synthetic English text for Shona
translation and silent synthetic PCM for Shona transcription, and checked the
configured model revisions and unreviewed transcription status. This verifies
the real Gateway client/service contract, not a translated framework-agent
conversation, speech recognition accuracy, or deployed service behavior. The
relay was stopped; the existing language service was left running.
The live language service and live Muse were then exercised together through
`EllaiUtteranceRunner` and `EllaiFrameworkExecutor`: a synthetic Shona greeting
was translated to English, processed by the framework agent, and its validated
answer translated back to Shona. With the live provider and local language
service fixture variables set, `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~RealLanguageServiceAndFrameworkHandleTranslatedConversation
--verbosity quiet` passed 1/1. This single-turn service integration does not
measure translation quality, prove multilingual history across turns, or test
the deployed Gateway; the disposable relay was stopped.
The live worker was also tested with two turns and an in-memory session store:
the first answer was translated into Shona, and the English follow-up correctly
recalled the synthetic word from the first user turn. The second turn's trace
confirmed that conversation history was included. With live Muse and the local
language service enabled, `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~FrameworkWorkerRecallsContextAfterTranslatedReply --verbosity quiet`
passed 1/1. This verifies one mixed-language exchange, not general translation
quality, a database restart, or deployed Gateway behavior. The relay was stopped.
The framework contract suite now also exercises report guidance through the
existing UI catalogue and broker: a framework function call discovers the
registered report controls and the final decision retains its `StartGuidance`
proposal. `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj
--no-restore -m:1 -nr:false --filter FullyQualifiedName~EllaiAgentFrameworkContractTests
--verbosity quiet` passed 67/67. This is a scripted model and application-layer
test; it does not establish live model guidance, client tour execution or
deployed Dynamic UI and Bit.Bonboarding behavior.
An additional scripted framework-to-Gateway-worker test verifies the guidance
proposal is committed to the in-memory conversation store, projected for its
owner, and absent from another owner's conversation read. The focused worker
and guidance admission HTTP suites passed 31/31 using `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
'FullyQualifiedName~EllaiUtteranceWorkerOutcomeTests|FullyQualifiedName~EllaiGuidanceAdmissionHttpTests'
--verbosity quiet`. These checks use an in-memory store and synthetic model;
they do not establish PostgreSQL persistence or live client tour execution.

| # | Demonstration | Current evidence and remaining limit |
|---|---|---|
| 1 | Ordinary conversation | Live Muse answered synthetic guest turns through a published local Gateway, PostgreSQL and rendered Chromium. The deployed Gateway and other clients remain unverified. |
| 2 | Framework-native tools | The framework invoked brokered tools in synthetic and live Muse tests. Complete provider protocol coverage remains open. |
| 3 | Authorized case records | Framework worker tests retrieved synthetic records from disposable PostgreSQL with the existing Qdrant exact index and rechecked revoked access. Production data and deployed routes remain unverified. |
| 4 | Verified aggregations | The broker and framework counted authorized disposable PostgreSQL records and rechecked membership; live Muse also exercised a count. General analytics and unsupported counts without a tool call remain open. |
| 5 | Multi-turn context | Live Muse guest turns recovered owner-scoped PostgreSQL history after a test-host restart and rendered Chromium refresh; a follow-up recalled a synthetic detail. Some tool observations and legacy traces remain intentionally unavailable for replay; complete parity is unverified. |
| 6 | Voice and text streaming | Framework streaming and synthetic speech through live transcription, Muse and local Kokoro were exercised. Validated text is buffered until the decision is complete; browser/device playback and latency acceptance remain open. |
| 7 | Reject unauthorized calls | Framework and PostgreSQL fixtures exercised denied tools and revoked case access. Independent security review and deployed authorization remain open. |
| 8 | Invalid or unavailable tools | Synthetic malformed-tool tests and disposable stopped-Qdrant tests fail safely or use the existing PostgreSQL fallback. Other provider and service failure modes remain open. |
| 9 | Timeout and cancellation | Explicitly classified read-only Gateway tools and the state tool's authorized queue-read phase return at cancellation/deadline even if a read ignores cancellation; late results are withheld and at most eight operations can remain active. State reference writes, unclassified tools and direct executor calls remain cooperative. The underlying operation is not forcibly terminated; deployed validation and independent review remain open. |
| 10 | Existing integration and regression tests | After the session-reminder registration fix, the default application suite passed 1,156 with 13 skipped and the latest integration suite passed 748 with 304 skipped. Selected opt-in fixtures passed separately; one live 1,024-token follow-up failed twice. Full regression acceptance remains open. |

The Gateway runs explicitly classified read-only tools in independently owned
scopes through a process-wide pool of eight. Cancellation or the existing tool deadline stops
waiting and rejects late results; the operation retains its scope and pool slot
until it actually finishes. This deliberately limits resource accumulation but
does not forcibly stop a noncooperative operation or guarantee host-shutdown
completion. A synthetic framework-worker test held a case read beyond its
deadline, observed a terminal failure before releasing the read, then confirmed
no late answer was published. Pool cancellation, saturation and fault-release
tests and the affected worker/HTTP regression filter passed **32/32** with
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiIsolatedToolCallsTests|FullyQualifiedName~EllaiUtteranceWorkerOutcomeTests|FullyQualifiedName~GuestHttpTurnsReachFrameworkWorkerAndRetainConversation' --logger 'console;verbosity=quiet'`.
Independent security and production load review are not verified.

Follow-up review found that the `ellai_state` queue projection can write
session case-result references. It and all unclassified tools therefore stay
on the cooperative path; only an explicit allowlist of read-only tools may
outlive a cancelled turn. The held-operation worker test now uses the
authorized case-count read. Classification and affected framework-worker
tests passed **17/17**. A noncooperative state query may still hold its turn
open; this preserves session persistence semantics until a fenced write design
is implemented. The authorized queue-read phase is now separately isolated in
its own scope through the existing `ICaseQueueQuery`; cancellation stops
waiting before the broker can begin a case-result reference write. The broker
uses the isolated queue only when its original queue is the same scoped DI
service, preserving custom broker configuration. State reference writes remain
cooperative after they start. Held state and count read tests passed, including
no late topic write and a succeeding follow-up; the existing PostgreSQL worker
case-list/history test passed **1/1** using a disposable intake database and a
synthetic model. This is not a hard stop for a noncooperative write.

After the queue-read adapter change, the isolation/worker/guest HTTP filter
passed **41/41**, including forwarding the original actor, limit and filter;
the application broker/framework filter passed **109/109**. The held state
read test checks that a follow-up completes before the old read is released
and that no topic is written after cancellation. These are synthetic fixtures
and do not constitute an independent security review.

An additional disposable PostgreSQL test passed **1/1** through the isolated
filtered queue adapter: the permitted case was returned before grant expiry,
and a successful authorized empty result omitted it afterward. The fixture
container was stopped. This does not establish deployed authorization or an
independent security review.

The worker isolation tests also verify that a broker explicitly configured
with a different queue keeps using that queue instead of the container's
registration. The custom-queue, timeout and isolation filter passed **13/13**;
this protects the existing server-side integration contract while using the
isolated adapter only for the matching scoped queue.

After restricting detachment, the disposable PostgreSQL/Qdrant framework
retrieval test and the existing-session threshold test passed **2/2**. Both
fixture containers were stopped. The retrieval test used a synthetic model.

After introducing the isolated-call boundary, the synthetic and live Muse
framework-worker retrieval tests passed **2/2** with disposable PostgreSQL and
Qdrant, including Qdrant outage and revoked-access follow-ups. The default
integration suite then passed **748**, failed **0**, and skipped **304** with
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --logger 'console;verbosity=quiet'`.
The test containers were stopped. These checks do not forcibly terminate the
held dependency or verify deployed behavior.

The isolated-call boundary now checks cancellation after a completed wait and
preserves cancellation if the operation faults concurrently. Synthetic return
and fault races, pool recovery, the held-read worker case and ordinary worker
tool paths passed **12/12** with `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiIsolatedToolCallsTests|FullyQualifiedName~FrameworkWorkerFinishesTimedOutTurnBeforeNoncooperativeReadReturns|FullyQualifiedName~WorkerRunsFrameworkExecutorAndPersistsAndStreamsAnswer' --logger 'console;verbosity=quiet'`.

The same filter now passes **13/13** after adding an actual DI-scope lifetime
test: cancellation returns to the caller while the held operation's scoped
resource stays alive, and that resource is disposed when the operation exits.
This is a focused self-review, not an independent security review.

The held-read worker test also submits a second turn in the same session before
releasing the timed-out read. The second turn completes, and the original late
read does not publish an answer. The focused isolated-call and worker timeout
filter passed **6/6** with `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~FrameworkWorkerFinishesTimedOutTurnBeforeNoncooperativeReadReturns|FullyQualifiedName~EllaiIsolatedToolCallsTests' --logger 'console;verbosity=minimal'`.
The test uses in-memory session persistence and a synthetic model.

On caller cancellation, the framework executor now checks the cancellation
token immediately after a tool returns, before recording its observation or
publishing an accepted-observation event. A synthetic test cancels a tool that
ignores cancellation, then releases it and confirms no accepted event or
subsequent model call. `dotnet test
tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore
-m:1 -nr:false --filter 'FullyQualifiedName~EllaiAgentFrameworkContractTests'
--verbosity quiet` passed 64/64. This does not establish a hard deadline: the
executor still waits for a noncooperative tool to return before unwinding its
scope.
The case-queue tool also checks cancellation immediately after its queue read
and before writing session recall references; the memory service checks it
before starting the write. A synthetic late-returning queue test confirms a
cancelled read does not create a recall topic. The focused queue and framework
contract suites passed 90/90. A dependency already inside a write that ignores
cancellation can still run late, so a hard timeout is not established.
An additional synthetic framework-executor test held the broker's queue
dependency until its deadline cancelled, then released it. The run returned
unavailable and no recall topic was written; the focused queue suite passed
26/26. The executor still waits for that dependency to return.
The pending-progress timer now also stops when the tool deadline expires,
so a noncooperative tool does not report new pending progress after timeout.
The framework contract suite passed 65/65, including a held tool observed past
the normal eight-second progress interval. The tool still must return before
its scoped dependencies can be released.

Client regression evidence on the current worktree: `dotnet test
tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false
-p:WasmBuildNative=false --logger 'console;verbosity=minimal'` passed 547/547.
These are test-host checks, not a deployed browser or device journey.

On the current worktree the application suite passed 1,116 tests with 11
opt-in skips. A default Gateway build failed at the browser native WebAssembly
link step because `emcc` exited 9009; the Gateway build with
`-p:WasmBuildNative=false` passed with no warnings or errors. A focused worker,
registration and authorization run initially had one intermittent outcome
test failure; that test previously read the transient feed immediately after
the durable commit and used the full default budget for repeated invalid model
responses. It now waits for reconciliation and uses a bounded synthetic test
budget. The focused rerun passed 54, skipped 3, with no failures. This does
not establish that the earlier failure's precise cause was proven.
The .NET 10 Emscripten pack is present and `emcc --version` succeeds when its
path and tool environment are set for the shell. A process-only build attempt
with those paths, and a second attempt supplying the SDK tool-path properties,
still failed in MSBuild with exit 9009. `dotnet --info` also reported a Windows
workload-installer type-initialization exception. No machine-wide or project
toolchain configuration was changed; native WebAssembly build remains unverified.
The build failure was subsequently traced to the process environment's empty
`OS` variable: MSBuild chose `:` rather than `;` for the Windows executable
search path. With `$env:OS='Windows_NT'` set only in the build process,
`dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -m:1
-nr:false --verbosity quiet` passed with 0 warnings and 0 errors, including the
native WebAssembly link. `dotnet workload list` still failed with the separate
Windows installer initialization error. No machine-wide setting was changed.

Client build evidence on the current worktree: `dotnet build
src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -f net10.0-android --no-restore
-m:1 -nr:false --verbosity minimal` passed with zero warnings and errors.
This verifies compilation and packaging on this Windows environment, not an
installed Android app or the migrated Gateway. `dotnet workload list` failed
with a Windows installer type-initializer exception; the Android build itself
nevertheless completed. iOS build and Android/iOS device behavior remain
**NOT VERIFIED**.

`FrameworkWorkerDoesNotPublishAfterSessionCancellationDuringModelCall` passed
1/1 with the actual worker, synthetic model and disposable PostGIS. After an
authorized earlier turn, the test paused the follow-up at the model adapter,
cancelled its session in PostgreSQL, then released the model. The durable turn
was `Cancelled` with no answer or tool trace, and no provisional chunk for that
turn appeared in the response store. This fixture updates the synthetic session
directly; it does not test a browser cancel action, a provider that ignores
cancellation, or cancellation after text has already streamed. Run with the
disposable intake fixture enabled and filter on the test name.

The uncommitted history checker preserves a turn whose `ellai_state` observation
is the broker's exact fixed feature-projection response. The server trace marks
that response version; old unmarked traces, queue projections and unknown
future response shapes do not use this exception. An actual broker and history
checker test is included in the application suite, which passed 1,045 tests
with 10 live probes skipped after this change. This does not authorize a feature
or establish current UI entitlement. Worker, live-provider and independent
security review remain **NOT VERIFIED**.

`ConcurrentFrameworkWorkersKeepDurableSessionHistoryIsolated` passed 1/1
against disposable PostGIS with a synthetic model. Two worker instances
claimed separate sessions and were held at the model boundary until both were
active. Each captured request contained its own prior-session marker and not
the other's; both outcomes persisted. The prior exchanges were seeded, and
both sessions used the same synthetic actor. This verifies a concurrent worker
fixture, not separate Gateway processes, live Muse or cross-actor isolation.
Run with the disposable intake PostgreSQL fixture enabled and filter on that
test name.

The test was then extended to two different synthetic actors and renamed
`ConcurrentFrameworkWorkersKeepDifferentActorsDurableSessionHistoryIsolated`.
It passed 1/1 against disposable PostGIS: both concurrent model requests
contained only their own seeded prior marker, each owner could read both of
their own turns, and cross-owner reads returned no turns. The actual Gateway
process, live provider concurrency, and cross-tenant case retrieval were not
tested by this fixture.
`LiveMuseConcurrentFrameworkWorkersKeepDifferentActorsDurableSessionHistoryIsolated`
and its synthetic control then passed 2/2 with disposable PostGIS and configured
live Muse. Both workers reached the model boundary before either was released;
all captured provider requests, including any repair, contained exactly one
actor's marker, both outcomes persisted, and cross-owner conversation reads
returned no turns. Prior turns were seeded and identifiers were synthetic. This
does not test separate Gateway processes or cross-tenant case retrieval. Run
with `ELLAI_LIVE=1`, the disposable intake fixture, configured provider settings,
and filter on `ConcurrentFrameworkWorkersKeepDifferentActorsDurableSessionHistoryIsolated`.
That synthetic and live Muse pair passed 2/2 again on the current source using
the same filter and a task-owned PostGIS server. The server used temporary
filesystem storage and was removed after the run.

On the current uncommitted worktree, `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --logger 'console;verbosity=quiet'`
failed: 708 passed, 290 skipped, 4 failed. A focused rerun of the four failures
also failed: service-provider validation could not resolve
`UserManager<SwiftAidUser>` for `SessionReminderChannelSender` in the three
`CaseCommandProcessorDependencyInjectionTests` and one
`ProtectedPayloadKeyRingSecurityTests` case. These failures are in the session
reminder registration/test service graph; their relationship to the migration
is not established, and the unrelated subsystem was not changed. The full
integration-regression acceptance criterion remains unmet. Skipped opt-in
fixtures are not counted as passes.

A later rerun after the framework history and worker changes also failed:
710 passed, 289 skipped and the same four tests failed, using `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --logger 'console;verbosity=minimal'`.
A focused `--no-build --no-restore` rerun of
`CaseCommandProcessorDependencyInjectionTests|ProtectedPayloadKeyRingSecurityTests`
failed the same four and passed 17. These failures remain outside the scoped
Ellai migration; the full regression acceptance criterion is not met.

On the deadline worktree, the full integration suite again failed with 722
passed, 296 skipped and the same four failures. Source inspection shows that
case-command registration adds community-session reminder services even when
Identity is disabled, while `SessionReminderChannelSender` requires
`UserManager<SwiftAidUser>`, registered only when Identity is enabled. This is
an actual service-graph mismatch for that configuration; it is not evidence
that the migrated Ellai path caused it. The migration-focused registration,
worker, prepared-audio, memory-index, voice-transcription, and privileged-route
tests passed 82 with 12 opt-in skips using `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
"FullyQualifiedName~EllaiReasoningRegistrationTests|FullyQualifiedName~EllaiUtteranceWorkerOutcomeTests|FullyQualifiedName~EllaiPreparedResponseAudioTests|FullyQualifiedName~EllaiMemoryIndexTests|FullyQualifiedName~EllaiVoiceTranscriptionServiceTests|FullyQualifiedName~EllaiPrivilegedRouteAuthorizationTests"`.
Gateway build passed with zero errors and the application suite passed 1,097
with 10 live probes skipped. These results do not prove all features work.
On this same deadline worktree, three opt-in Qdrant tests passed using a
disposable migrated PostgreSQL database and disposable Qdrant: framework
worker recall with current authorization and index outage, exact-index access
rechecking, and vectorless scope isolation. Three Redis tests passed using a
disposable Redis, an explicitly stopped disposable endpoint, and PostgreSQL:
cache isolation/expiry/removal, explicit outage failures, and framework session
recovery while Redis was unavailable. All task-created containers were
removed. These are synthetic fixtures, not deployed infrastructure acceptance.
On this worktree, live Muse probes passed 4/4 across provider streaming,
framework brokered-tool execution, the framework runner's streamed tool
decision, and dependent tool calls. The first attempted runner probe stopped
before any provider request because its process configuration omitted ASR
settings; loading the complete existing Ellai settings fixed the fixture.
Inputs were synthetic. These probes do not prove browser, device, or deployed
Gateway behavior.
The live recorded-voice-to-Muse-to-Kokoro and synthetic-speech-to-live-ASR-
Muse-to-Kokoro tests were rerun after the worker changes and passed 2/2.
They use an in-memory session and a task-owned loopback relay to the existing
speech service; the relay was removed. Actual microphone capture, browser or
device playback, and incremental model-time answer streaming remain unverified.
The full synthetic-speech fixture was rerun on the current code and passed 1/1:
local Kokoro output was transcribed by live ASR, processed by live Muse through
the framework worker, and synthesized again by the local speech service. It
used an in-memory session and a temporary loopback relay, which was stopped.
It does not verify microphone capture, browser/device playback or incremental
answer delivery during model generation.
After the lease and publication changes, the live Muse guest HTTP tests passed
2/2 for durable multi-turn recall and test-host restart. Live Muse tests also
passed 2/2 for permission-scoped case recall through Qdrant and an authorized
PostgreSQL case count. They used synthetic guest prompts and records, a test
host, and task-created disposable PostgreSQL and Qdrant; both containers were
removed. The tests do not establish deployed Gateway behavior or guarantee
model reliability for all prompts.

The actual local Gateway process was then run against a task-created loopback
PostgreSQL database migrated by the Gateway's `--migrate-database` command.
With Identity disabled, the first guest session request failed CSRF validation:
the Ellai client sends `X-CSRF-TOKEN`, while that configuration used the
framework default header. Gateway authentication registration now configures
the Ellai header regardless of Identity; the focused registration tests passed
7/7. After rebuilding, a real local HTTP guest session and two live Muse turns
completed; the second answer recalled the word from the first. The Gateway
and PostgreSQL fixture were stopped. The private fixture key directory was
retained because the environment rejected its removal. This used a disposable
database and synthetic text, not deployed infrastructure or browser/device
acceptance. Incremental text during model generation remains buffered until
validation and durable commit.
The Identity-disabled authentication fixture now also exercises a real
antiforgery cookie and the Ellai client's `X-CSRF-TOKEN` header: a valid
token is accepted and an invalid token rejected. The focused disabled-Identity
suite passed 8/8. This test uses ASP.NET Core TestServer and does not replace
browser verification.
The local process check was extended to restart the actual Gateway executable
between two live Muse turns. A guest cookie and antiforgery refresh were used
after restart, and the second answer recalled the first turn's synthetic word
from the same PostgreSQL-backed conversation. The loopback database was
created and migrated solely for this run and its container was removed.
This is one local restart scenario, not deployed multi-instance acceptance.
On the current source, `GuestHttpLiveMuseRecoversConversationAfterTestHostRestart`
passed 1/1 again using live Muse, synthetic guest text and fresh disposable
PostgreSQL. The test checks cookie-scoped response recovery after TestServer
recreation, rejection of a different guest, and recall of the first turn in
the second answer. The temporary database container was stopped. This is not
a deployed Gateway, browser, or device test.
The computer-use browser inventory was retried and failed before initialization
with `failed to write kernel assets` and Windows error 3. A Chromium cache and
an existing Playwright package are present locally, but no browser journey was
run in this check. The running Gateway was left untouched; browser acceptance
remains open pending an isolated current-build Gateway fixture.
That fixture was subsequently published and run on loopback with disposable
PostgreSQL and live Muse. The existing Chromium conversation test passed twice;
the second run sent a synthetic first turn, reloaded the page, then verified
the rendered follow-up recalled the requested word. It also checked thinking
animation, reduced motion, mobile status visibility and no page errors. The
Gateway and database were stopped. The debug publish reported WASM0001 SQLite
varargs warnings. This is one local Chromium journey, not deployed or device
acceptance, and answer text was first observed after generation completed.
The task-created publish output and synthetic certificate remain inside the
previously retained fixture directory after its deletion was blocked by policy.
An actual Chromium-to-Gateway audio-feed test initially failed because three
short validated sentences were combined into one response chunk. The framework
now splits at safe single-space sentence boundaries without the former minimum
length; joining the chunks still reconstructs the validated answer. The test
then passed with live Muse and local Kokoro: three chunks each returned 24 kHz
PCM for the configured voice. Framework contract tests passed 66/66 and the
worker audio suite passed 5/5. The Gateway, disposable database and speech relay
were stopped. This verifies HTTP audio delivery, not audible device playback;
answer chunks still wait for validation and durable commit.
The live microphone browser test also passed against the isolated published
Gateway, a freshly migrated disposable PostgreSQL database, live Muse and the
existing speech services. A synthetic Kokoro recording was supplied as the
Chromium microphone: one capture and one WebSocket produced one completed turn,
the spoken greeting appeared in durable conversation history, and a response
audio request was made. The test reported no page errors or duplicate utterance
submission. Command: `node tests/SwiftAid.Ui.Tests/ellai-live-voice.browser.cjs`
with `SWIFTAID_RUN_LIVE_ELLAI=1`, a synthetic WAV, one expected turn and the
loopback Gateway URL. The test stopped the session immediately after the
response; it does not establish audible playback, physical microphone behavior,
or deployed Gateway acceptance. The disposable Gateway, database and relay
were stopped; the synthetic WAV remains in the retained fixture directory.
The actual local Gateway was also run with a fresh migrated disposable database
for a live Muse public-facts tool turn. The guest HTTP request completed, the
response feed included the tool acknowledgement, and the stored PostgreSQL
trace included `ellai_productfacts`. The fixture used synthetic text; the
process and database container were stopped. This single tool invocation does
not prove every tool or permission configuration.
The live guest HTTP multi-turn test was extended and passed 1/1 with a fourth
turn following a product-facts tool answer. The follow-up recalled the topic,
and its durable trace retained the prior product-facts observation. This used
a test host, live Muse, synthetic prompts and a task-created migrated PostgreSQL
fixture; the fixture container was removed. It does not establish every tool's
history-replay behavior.
The UI suite was rerun on this worktree and passed 547/547 using `dotnet test
tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false
-p:WasmBuildNative=false --verbosity quiet`. Browser and device behavior are
not established by that suite. The Android target also built successfully with
`dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -f net10.0-android
--no-restore -m:1 -nr:false --verbosity quiet`; no device test was performed.

## Qdrant and Redis compatibility matrix — pre-cutover gate

With the current framework-only source, a disposable PostGIS/Qdrant/Redis run
passed 4/4: the framework worker recalled authorized synthetic case results
through the actual Qdrant index and PostgreSQL authority with synthetic and
live Muse models; the direct framework authority fixture and Redis prefix,
expiry and removal fixture also passed. The worker fixture simulated a Qdrant
transport failure and then rechecked revoked access. A second run stopped the
disposable Qdrant and Redis containers and passed 4/4: synthetic and live Muse
workers fell back to PostgreSQL with Qdrant stopped, Redis reads/writes/removes
reported failure, and a PostgreSQL-backed framework conversation recovered
while Redis was unavailable. The latter does not establish that Ellai uses
Redis as its session store. These runs used loopback fixtures, synthetic data,
`ELLAI_LIVE_QDRANT=1`, `ELLAI_LIVE=1`, the framework/intake PostgreSQL opt-ins,
and the named test filters in `EllaiMemoryIndexTests`, `CaseImportPostgresTests`,
`EllaiPostgresTests` and `RedisCacheIsolationTests`. The task-created containers
were removed; their anonymous PostgreSQL and Redis volumes were retained under
the repository's volume-preservation policy. Production services, transport,
cross-instance concurrency and deployed Gateway behavior remain **NOT VERIFIED**.
Both runs used `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --logger 'console;verbosity=minimal'`
with, respectively, filters
`FullyQualifiedName~FrameworkWorkerRecallsAuthorizedCaseFromPostgresAndQdrant|FullyQualifiedName~LiveMuseWorkerRecallsAuthorizedCaseFromPostgresAndQdrant|FullyQualifiedName~FrameworkRecallUsesPostgresAuthorityWithLiveQdrant|FullyQualifiedName~SeparateInstancePrefixesIsolateEntriesAndExpiryAndRemoval`
and
`FullyQualifiedName~FrameworkWorkerFallsBackToPostgresWhenQdrantIsStopped|FullyQualifiedName~LiveMuseFrameworkWorkerFallsBackToPostgresWhenQdrantIsStopped|FullyQualifiedName~FrameworkWorkerRecoversPostgresConversationWhileRedisIsUnavailable|FullyQualifiedName~UnavailableRedisReturnsExplicitFailureForReadWriteAndRemoval`.

The current code was retested with disposable PostgreSQL and Qdrant: the
framework worker's authorized case recall and the direct PostgreSQL authority
recheck passed 2/2 using a synthetic model. The first attempt failed before
retrieval because the fresh fixture database had not been migrated; after the
existing receipt fixture applied migrations, both tests passed. Both disposable
containers were stopped. This is not a deployed Gateway or live-model test.
The dedicated framework authority fixture now applies migrations before
seeding its disposable database. The same two tests passed 2/2 from a newly
created empty database and a fresh disposable Qdrant, without a prerequisite
test run; both containers were stopped.
The real PostgreSQL/Qdrant worker fixture was extended with a synthetic turn
that removes topic-tool admission while the case grant remains active. It
passed 1/1: the prior case identifier was absent from model history and no
additional Qdrant recall was attempted for that turn. The subsequent grant
expiry check also remained in the test. The disposable containers were stopped;
live model and deployed Gateway behavior remain unverified.
The live Muse variant was then rerun on the current code and passed 1/1 with
fresh disposable PostgreSQL and Qdrant: it retrieved the synthetic authorized
case, repeated the request during an injected Qdrant transport outage, and
excluded the case after its PostgreSQL grant expired. The tool-removal turn
above is synthetic-only. Both containers were stopped; this does not establish
deployed Gateway or browser behavior.

The current Redis cache isolation fixture passed 2/2 against a disposable Redis
instance and an unavailable loopback endpoint. It checked separate instance
prefixes, expiry and removal, plus explicit read/write/remove failure on outage.
The task-owned Redis container was stopped. This is not an Ellai session store
or deployed infrastructure test.

The current PostgreSQL worker fixtures initially failed after the history
allowlist change because follow-up test turns removed the very tool whose
continued authorization they intended to test. Those fixtures now retain that
tool on follow-up turns; the outage fixture also selects its own case by ID so
other synthetic test records do not affect it. A rerun passed 7 tests covering
Qdrant outage, grant expiry, cancellation, stale case/import lists and
permission changes during execution; one separate live Muse variant was
skipped. The disposable PostgreSQL container was stopped. These are fixture
results, not deployed Gateway acceptance.
After correcting the fixture, the live Muse Qdrant-down variant also passed
1/1 against fresh disposable PostgreSQL and an unavailable loopback endpoint.
It retrieved the synthetic case through the existing fallback and excluded it
after the grant expired. The database container was stopped; Qdrant was already
unavailable at the start of the test, and deployed outage behavior is not
established.

This matrix is based on the current source and the checks below, not on the
requirements documents alone. No infrastructure behavior or service ownership
is changed by the framework migration.

| Area | Current integration and contract | Framework compatibility and acceptance |
| --- | --- | --- |
| Qdrant exact Ellai recall | When Ellai and `Qdrant:Enabled` are enabled, after provider configuration validation, `Program.cs` registers `EllaiMemoryIndex` and `EllaiMemoryProjectionWorker`. `EllaiMemoryServiceCollectionExtensions` fixes the base collection to `ellai-memory`; the actual vectorless collection is `ellai-memory-exact`. `EllaiCaseResultMemory` uses `IEllaiExactReferenceIndex` only for the `case-results` reference; `ellai_topic` and `ellai_evidence` reach it through `EllaiToolBroker`. | Reuse those broker and index services. Framework tests exercise authorized recall, revoked access, index outage, and PostgreSQL rechecks; the current-branch live and synthetic worker results are recorded below. A deployed Gateway remains **NOT VERIFIED**. |
| Qdrant vector/semantic | `EllaiMemoryIndex` contains vector upsert/search methods with caller-provided vectors and session/epoch filters, but source search found no production caller of these methods or an Ellai embedding encoder. The active exact path uses no vectors. A vector adapter round trip is not proof of deployed semantic retrieval, RAG, embeddings or semantic cache. | Do not claim or enable semantic retrieval or a second pipeline. Any future encoder remains separately gated. Test existing exact behavior through the framework; verify any future semantic path independently before use. |
| Qdrant scope and lifecycle | Exact payload contains `memory_id`, `session_id`, `scope_epoch`, `topic_id`, `source_version`, `expires_at` and `encoding=exact/1`; point IDs derive from session, epoch and ID. The index filters and rechecks identity and expiry. `EllaiCaseResultMemory` verifies PostgreSQL topic/version, actor, scope, expiry and current authorized case query; it requests index cancellation after 500 ms and falls back to authoritative topic resolution on failure. That deadline is cooperative: an index implementation ignoring cancellation can delay fallback. Projection reads active PostgreSQL topics, retries failures and upserts every 30 seconds; expired/stale points are ignored rather than proven physically deleted. Qdrant is derived, not case authority. | Preserve these boundaries and 24-hour snapshot behavior. Verify stale, revoked, expiry, outage, cross-session and cross-scope cases with disposable services and actual framework execution; verify restart/reprojection and cleanup separately. Existing vector `SearchAsync` trusts the remote filter and should not be exposed as an authorized tool without independent scope and payload validation. |
| Qdrant transport and persistence | Requests send the API key in the `api-key` header. Local Compose uses a private Qdrant service and `qdrant-data` volume; plaintext is explicitly configured for Development. Configuration validation checks the enabled endpoint/key and transport policy, and Ellai registration now honors `Qdrant:Enabled`. | Verify production transport and credentials separately. Do not expose credentials, raw case narratives or arbitrary Qdrant access to the model. Deployments using the index must explicitly enable it. |
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

`FrameworkWorkerFallsBackToPostgresWhenQdrantIsStopped` passed 1/1 using a
synthetic model, disposable PostGIS and a task-owned Qdrant container. The
fixture verified Qdrant healthy, stopped that container, verified its loopback
endpoint was unavailable, then ran the actual framework worker and existing
`ellai_topic` broker. It confirmed the worker attempted the Qdrant scroll and
returned the currently authorized synthetic case from the PostgreSQL-backed
topic/queue path. The first fixture attempt expected a connection exception but
the stopped endpoint timed out; the fixture was corrected to accept either
transport failure and the rerun passed. This test starts with an unavailable
index; it does not test a populated index stopping mid-turn, Qdrant restart,
live Muse or a deployed Gateway. Run with the disposable intake PostgreSQL
fixture and `QDRANT_TEST_DOWN_ENDPOINT` pointing to the stopped task-owned
loopback endpoint, filtering on the test name.
`LiveMuseFrameworkWorkerFallsBackToPostgresWhenQdrantIsStopped` and its
synthetic-model control subsequently passed 2/2 against disposable PostGIS and
a stopped task-owned Qdrant container. The live framework called the existing
topic tool, attempted the unavailable index and retained evidence for the
authorized synthetic case via PostgreSQL. This starts with Qdrant down; it is
not evidence of mid-turn failure, restart, deployed Gateway or production data.
Run with `ELLAI_LIVE=1`, provider settings, the disposable intake fixture and
`QDRANT_TEST_DOWN_ENDPOINT`, filtering on
`FrameworkWorkerFallsBackToPostgresWhenQdrantIsStopped`.
The same live and synthetic tests were extended to expire the PostgreSQL
workspace grant while Qdrant remained stopped, then call the topic tool again.
Both passed 2/2: a second index attempt occurred, and the follow-up model text,
answer and stored tool evidence did not contain the revoked synthetic case ID.
The fixture uses a stopped index from the start and cannot prove mid-turn outage
or atomic revocation. An initial build failed due to duplicate local variable
names in the test; those were corrected before the passing run.
The uncommitted trace now carries the original case-result snapshot expiry as
internal, model-hidden provenance. Replay rejects expired or older unmarked
case-recall traces even when the underlying case version is unchanged. The
actual broker test verifies capture and that the field is excluded from tool
JSON; a focused application test covers unexpired, expired and missing expiry.
The full application suite passed 1,046 with 10 skips, and the stopped-Qdrant
live Muse and synthetic worker tests passed 2/2 after the change, including
an assertion that the worker persisted the expiry. A real 24-hour elapsed
worker run and independent security review remain **NOT VERIFIED**.

**Previously authorized content in conversation history:** An earlier run of
`FrameworkWorkerRecallsAuthorizedCaseFromPostgresAndQdrant` demonstrated
that, after the synthetic PostgreSQL grant expired, fresh Qdrant/topic recall
omitted the case identifier but the next framework model request still
contained it in an earlier assistant answer. That run used a
synthetic model and disposable PostgreSQL/Qdrant stores; no real case data or
live provider was involved. The prior implementation replayed completed
user/assistant text without retained tool evidence, so a fresh-recall check
alone did not establish that revoked content was absent from model context.
The opt-in test uses
`SWIFTAID_RUN_FRAMEWORK_POSTGRES_FIXTURES=1`, `ELLAI_LIVE_QDRANT=1`, dedicated
loopback PostgreSQL and Qdrant endpoints, and `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~FrameworkWorkerRecallsAuthorizedCaseFromPostgresAndQdrant`.

As a partial scope-change safeguard, utterance admission now rejects a known
workspace or identity-help session when its stored epoch differs from the
authenticated principal's epoch, before queuing work. The user must start a
session under the current scope. Older or nonstandard scope labels retain their
existing behavior. Focused submission/framework tests passed 52/52; guest-route
and voice integration tests passed 39 with six opt-in tests skipped. This
comparison does not independently refresh the principal or detect a grant
expiry within the same epoch.

**Tool trace and replay checks:** framework execution now
captures the server-observed tool name, result status, evidence references and
truncation flag, independent of the model's optional citations. The worker
stores a versioned, bounded trace with the completed turn in PostgreSQL; version
2 records the prior turns actually supplied. It excludes tool arguments and result text. Legacy,
oversized, unsupported or incomplete traces remain null/unknown and must not be
treated as public or authorized. The trace is not returned in the conversation
client projection. The additive `20261103190000_EllaiTurnToolTrace` migration
must be applied before the new Gateway code; its Down removes the trace column
and all trace data. No backfill or new store is introduced. Other sensitive tool
types and legacy turns still need compatible handling before cutover.

The migration was applied, rolled back and reapplied on a disposable PostGIS
database. The real PostgreSQL/Qdrant worker and restart fixtures passed 2/2,
including persisted tool evidence and the history-included marker. EF's pending
model check passed. Application tests passed 1,018 with 10 skips; focused
guest, voice and memory integration tests passed 46 with 12 live tests skipped.
Framework replay now checks recorded case-queue references through the existing
authorized query and requires the recorded versions to match. It also verifies
product-fact references against the current catalog, checks the static ingestion
schema's current evidence revision, and retains successful input-only format
validation. Documentation search/get now return section and source-revision
evidence; replay uses the existing approved documentation service and omits
answers if the section or revision is unavailable or changed. Existing older
documentation turns without references remain unknown. The ingestion-schema revision must be updated when its content
changes. Version 2 traces record
the exact retained prior turn IDs so these checks include transitive context;
version 1 traces with inherited history are treated as unknown because their
exact dependencies are unavailable. Missing, malformed,
truncated, unsupported and legacy traces are omitted from framework context,
with a clarification marker. The durable history is unchanged. A previously
omitted turn does not taint a later turn that did not receive it. This is a
deliberate compatibility limitation: other tool types and legacy history are
not yet reauthorized and therefore lose model context. The checks occur before
dispatch and are not atomic with provider transmission or later revocation.

The synthetic-model worker test now verifies the authorized case appears in
history before revocation and is absent from the next model request after the
PostgreSQL grant expires; Qdrant outage still falls back to PostgreSQL. It,
the PostgreSQL worker restart test and guest TestServer restart test passed
3/3 against disposable stores. A first combined run failed because test
workers in different classes consumed each other's shared outbox; those
fixture classes now share a serialized xUnit collection and the combined rerun
passed. Application tests passed 1,025 with 10 skips. Live provider, deployed
Gateway, all other tools, legacy compatibility and atomic revocation remain
**NOT VERIFIED**, and the cutover gate remains closed.
Subsequent focused tests passed 48/48 after adding fail-closed handling for
version 1 inherited history and tests for missing or forward references and
caller cancellation. These checks do not replace independent security review.
After adding schema, format-validation and other-actor cases, application tests
passed 1,031 with 10 skips; the guest, voice and memory integration suites
passed 46 with 12 opt-in skips. These do not establish live-provider replay
compatibility for the added cases.
Following the documentation change, the focused documentation and framework
suite passed 62/62; the complete application suite passed 1,032 with 10 skips,
and guest/voice/memory integration tests passed 46 with 12 opt-in skips.
Import-status evidence can now be rechecked through the existing owner- and
workspace-scoped `ICaseImportStore.GetAsync`, requiring an unchanged timestamp
and a terminal `Completed` or `Failed` state. `Running`
and `Paused` remain omitted: the store can derive `Paused` from an expired
lease without changing the timestamp. Focused synthetic tests cover owner,
revocation, changed timestamp and nonterminal states; they passed with the
framework suite (63/63). Application tests passed 1,033 with 10 skips and
guest/voice/memory tests passed 46 with 12 skips.
`FrameworkImportHistoryRequiresCurrentPostgresOwnerScopeAndVersion` then passed
1/1 using a task-owned PostGIS database and synthetic import: the history checker
retained an unchanged authorized import, rejected another actor and an updated
version, and omitted it after the workspace grant expired. Run with
`SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES=1`, a disposable loopback
`swiftaid_intake_test` connection in `SWIFTAID_TEST_INTAKE_POSTGRES_CONNECTION`,
and `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~FrameworkImportHistoryRequiresCurrentPostgresOwnerScopeAndVersion`.
The fixture also runs `EllaiUtteranceRunner` and the Microsoft Agent Framework
executor with a synthetic model and PostgreSQL session/outbox. It reloads the
saved tool trace through a new database context: the model receives the earlier
import answer before grant expiry and does not receive it afterward. The durable
fixture passed twice against the same disposable database and deletes its own
session. An earlier in-memory-session attempt failed before the test body
because the disposable database was still starting; the rerun after
`pg_isready` passed. `FrameworkWorkerRechecksDurableImportHistoryAfterGrantExpiry`
subsequently passed with the actual utterance worker, Gateway framework
registration, PostgreSQL session/outbox/import store and a synthetic model. The
worker's first model request contained the authorized import reference; after
grant expiry its next request omitted that reference and its dependent answer.
The worker test and direct durable-session test passed together 2/2 against the
disposable database. Concurrent changes and deployed Gateway behavior remain
**NOT VERIFIED**.
`LiveMuseWorkerRechecksDurableImportHistoryAfterGrantExpiry` then passed with
the configured live Muse route, actual worker and disposable PostgreSQL; the
synthetic-model control passed in the same run (2/2). A recording wrapper at
the model adapter verified the synthetic import reference was present before
the grant expired and absent from the later model request. That initial run
seeded the prior turn and trace. The updated live and synthetic-model fixtures
then passed together again (2/2) with the framework invoking the existing
`ellai_import_status` tool against PostgreSQL. The worker persisted its
observed import evidence and answer, replayed the authorized conversation, and
omitted the reference after grant expiry. Use `ELLAI_LIVE=1` alongside the
intake PostgreSQL fixture settings and filter on this test name. Concurrent
grant changes and a deployed Gateway remain **NOT VERIFIED**.

The same live and synthetic worker tests were rerun after the recording adapter
was strengthened to inspect every model request in each turn, including any
repair request; both passed (2/2). This does not close the concurrent-revocation
or deployed-Gateway gates.

**Long-session continuity gap:** PostgreSQL's `ReadConversationAsync` reads at
most the latest 100 turns, including the current turn. Framework history
authorization recursively checks the prior-turn IDs recorded in each trace.
Consequently, a sufficiently long chain of inherited turns can reach a turn
outside that window and fail closed, omitting even recent dependent exchanges.
The application test `LongConversationLosesInheritedContextWhenTheOldestDependencyFallsOutsideTheStoreWindow`
passed (1/1) using a synthetic 102-turn chain and a 100-row window; it confirms
this loss of continuity, not a successful long-session acceptance test. A
bounded, authorization-preserving solution and a durable worker-level test are
required before multi-turn parity or Gateway cutover can be accepted.

An uncommitted version 3 trace now carries forward the complete observations of
authorized history, initially deduplicated within a 256-observation and 60 KB
limits. Every subsequent replay rechecks those observations against current
authority. Version 2 dependencies remain subject to the 100-row limitation;
an oversized or incomplete trace is omitted on later replay rather than being
treated as authorized. The application
test `FlattenedProvenancePreservesLongHistoryAndStillRechecksOriginalCaseAccess`
passed across 110 synthetic turns and removed dependent history after case
access was revoked. The focused authorization and trace suite passed 19/19.
The actual worker with PostgreSQL passed 2/2 using live Muse and a synthetic
model: an authorized import observation was persisted into the next turn's
version 3 trace and was absent after the grant expired. Atomic revocation and
independent security review remain cutover gates.
After adding the version 3 limit and deduplication check, the complete
application suite passed 1,036 tests with 10 live probes skipped using
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj
--no-restore -m:1 -nr:false`.
`FrameworkWorkerRechecksImportHistoryBeyondThePostgresReadWindow` then passed
1/1 with a synthetic model, actual worker and disposable PostGIS. The framework
invoked the import-status tool and the worker persisted its version 3 evidence;
the fixture inserted 101 synthetic completed exchanges carrying that trace,
confirmed the original turn was outside the 100-row read, and observed the
import reference in the next worker request while authorized but not after
the workspace grant expired. The intervening turns were seeded, not 101 actual
provider executions. Run with `SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES=1`, a
disposable `SWIFTAID_TEST_INTAKE_POSTGRES_CONNECTION`, and `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~FrameworkWorkerRechecksImportHistoryBeyondThePostgresReadWindow`.
Long-running live-provider conversations and concurrent grant changes remain
**NOT VERIFIED**.

**Rollback security blocker:** source inspection of committed revision `3ec6eb1`
shows that its trace validator accepts version 1 only, but its utterance runner
does not consult trace validation when preparing model history: it calls
`EllaiConversationContext.Build` directly. Thus rolling back to that revision
can replay earlier case or import text after access is revoked, including text
from turns written with version 3 traces. The existing regression recorded in
commit `ea4cfae` demonstrated this for case history before the new authorization
work. Before the current uncommitted changes, switching to the legacy executor
also did not activate the history checker. A rollback or mixed-version deployment must
not be treated as authorization-equivalent until a safe operational and code
path is implemented and tested; no deployed rollback has been tested here.
The focused test `VersionTwoDependenciesAreCarriedIntoVersionThreeAndRecheckedAfterOldRowsDisappear`
passed as part of the 21/21 authorization and trace suite: it checks forward
conversion of available version 2 dependencies, retention after the old rows
disappear, and subsequent revocation. It does not demonstrate rollback safety.

Before the user removed the rollback requirement, the uncommitted legacy
executor was adapted to record bounded tool observations and use the same
current-authority history checker as framework execution.
Untraced older exchanges are omitted from its model context, reducing continuity
for those exchanges. `LegacyWorkerRechecksDurableImportHistoryAfterGrantExpiry`
and the framework control passed 2/2 against disposable PostGIS with synthetic
models: the worker invoked the real import-status tool, saved its evidence,
included the authorized exchange, and excluded it after workspace grant expiry.
The initial integration build failed on a malformed test string and passed after
that fixture was corrected. This supports an execution-mode switch within the
updated build; it does not make an older binary or mixed-version rollout safe.
Run using the intake PostgreSQL fixture and filter for the two worker tests.
The synthetic-model `FrameworkToLegacyWorkerSwitchRechecksPersistedImportHistory`
and `LegacyToFrameworkWorkerSwitchRechecksPersistedImportHistory` tests then
passed together 2/2 against disposable PostGIS. Each stops one worker after a
real import-status tool call, starts a new worker with the other executor in
the same test process, and checks authorized replay followed by omission after
grant expiry. These are configuration switches using the updated code in one
process; they are not tests of an older binary, concurrent mixed-version
deployment, deployed Gateway restart, or live-provider switching. Command:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
'FullyQualifiedName~FrameworkToLegacyWorkerSwitchRechecksPersistedImportHistory|FullyQualifiedName~LegacyToFrameworkWorkerSwitchRechecksPersistedImportHistory'`
with the disposable intake PostgreSQL fixture enabled.

The uncommitted utterance runner now rechecks every retained history turn at
execution fences, including the framework model-call guard and legacy iteration
gate. `RunnerStopsBeforeModelWhenAccessChangesAfterHistorySelection` passed
2/2 with a synthetic case query that permits the first check and revokes the
next; neither executor called its synthetic model. The complete application
suite passed 1,039 with 10 live probes skipped. The two executor-switch tests
and the long-window worker test passed 3/3 against disposable PostGIS with
synthetic models after this change. This narrows the interval between a history
check and a provider call; it cannot make the database check atomic with an
external request. Rechecking at fences adds authority-store reads, including
during streaming, and latency/load has not been measured. Real concurrent
revocation, in-flight provider behavior, and independent security review remain
**NOT VERIFIED**.
`FrameworkWorkerStopsWhenGrantExpiresAfterHistorySelection` and
`LegacyWorkerStopsWhenGrantExpiresAfterHistorySelection` then passed 2/2 with
the actual worker, disposable PostGIS and synthetic models. A test-only store
wrapper paused after an authorized import read, the fixture expired the real
workspace grant in PostgreSQL, and the worker resumed: both turns ended in
terminal failure without a model request or assistant response. Command:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~WorkerStopsWhenGrantExpiresAfterHistorySelection`, with the
disposable intake PostgreSQL fixture enabled. This covers the controlled
selection-to-fence race only; it does not retract an already sent provider
request or prove atomic authorization, production performance or deployment.
The uncommitted runner now also rechecks authority after model execution and
any output translation before returning a completed outcome; this closes a
legacy-loop path that could otherwise persist an answer after its authority
changed during the provider call. `LegacyRunnerDoesNotPublishWhenAccessChangesDuringModelCall`
passed with a synthetic case query and completion. The framework and legacy
`WorkerWithholdsAnswerWhenGrantExpiresDuringModelCall` fixtures passed 2/2 with
disposable PostGIS and gated synthetic models: the model adapter had already
received the authorized import history, the PostgreSQL grant then expired,
and the worker stored a terminal failure without an assistant answer. These
tests do not retract already sent data or previously streamed text and do not
make the final check atomic with database persistence. Command: `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~WorkerWithholdsAnswerWhenGrantExpiresDuringModelCall` with
the intake fixture enabled. The complete application suite passed 1,040 with
10 live probes skipped after this change.
Within each history authorization pass, repeated case ID/version and import
ID/version checks now share their result. The cache exists only for that pass;
later execution fences query the source again. Synthetic tests confirmed six
turns sharing one case or import caused one authority read per pass and that a
subsequent pass detected revocation. The full application suite passed 1,042
with 10 skips. Five PostgreSQL worker tests covering the pre-model grant race,
in-flight grant expiry and the long history window passed 5/5 after this change
using disposable PostGIS and synthetic models. This is a query-count check, not
a production latency or load measurement; revocation can still occur between
the source read and an external call or persistence.

**Recent-import list freshness gap:** the `ellai_imports` trace captures the
IDs and versions of returned jobs, but not the workspace query or a revision
for the complete result set. Rechecking those individual jobs cannot detect a
new job added to the same workspace. `RecentImportHistoryCanRemainReplayableAfterAnotherImportIsAdded`
passed as a characterization test against disposable PostGIS: a one-item
historical list remained replayable after the actual recent-import list grew to
two items. The earlier statement remains a historical statement; the test does
not show the model claiming it is current. Current list completeness and
answers to follow-up questions such as “what about now?” are **NOT VERIFIED**.
The uncommitted fix retains only the workspace, organization and country scope
from an allowed list call in internal tool provenance. Before replay, it uses
the existing owner-scoped `GetRecentAsync` and compares ordered IDs, versions,
scope and terminal state. Older list traces without scope, unavailable scope,
truncated lists and changed results are omitted. The PostgreSQL test
`RecentImportHistoryRequiresTheSameAuthorizedCompleteList` passed after verifying
an unchanged list replays, a different actor and old unscoped traces do not,
and a newly added import invalidates the old list. That test and four worker
regressions passed 5/5; the complete application suite passed 1,043 with 10
skips. An initial fixture run failed because its import context lacked the
organization ID, and a subsequent build failed an xUnit analyzer; both were
corrected before the passing rerun. The direct checker test seeds provenance.
Subsequently, `FrameworkWorkerRechecksRecentImportListAfterItChanges` and
`LegacyWorkerRechecksRecentImportListAfterItChanges` passed 2/2 against
disposable PostGIS with synthetic models. Each actual worker called the real
`ellai_imports` broker tool, persisted its scope and evidence, replayed the
unchanged history, then omitted it after a second import was added. Run with
the intake fixture enabled and filter on
`FullyQualifiedName~WorkerRechecksRecentImportListAfterItChanges`.
`LiveMuseWorkerRechecksRecentImportListAfterItChanges` and the synthetic
framework control subsequently passed 2/2 using live Muse, disposable
PostGIS and synthetic imports. The actual framework tool call recorded the
scope and evidence; adapter-level capture showed the authorized prior
conversation present before a second import and omitted from all captured
follow-up model requests after the list changed. Run with `ELLAI_LIVE=1`,
the disposable intake fixture and configured Ellai provider settings, using
filter `FullyQualifiedName~LiveMuseWorkerRechecksRecentImportListAfterItChanges|FullyQualifiedName~FrameworkWorkerRechecksRecentImportListAfterItChanges`.
This is a worker fixture, not a deployed Gateway or concurrent-mutation proof.
Independent security review remains **NOT VERIFIED**. The query recheck is
not atomic with a provider request or persistence.

The same membership issue applies to `ellai_state(projection=queue)`: unchanged
individual case versions do not prove the original queue still contains the
same rows. The uncommitted trace now records only the existing bounded queue
options; replay reruns `ICaseQueueQuery` under the current actor and compares
the complete ordered IDs and versions. Old traces without query options,
unavailable queries and truncated or changed lists are omitted. Synthetic
application tests cover query-option capture, changed membership and missing
provenance. `CaseListHistoryRequiresTheSameAuthorizedPostgresQueryResult`
passed 1/1 against disposable PostGIS with synthetic cases: the prior list was
retained unchanged and omitted after another authorized case was registered.
Run with the disposable intake fixture and filter on that test name. The full
application suite passed 1,045 with 10 skips. This direct checker test seeds
the trace. `FrameworkWorkerRechecksCaseListAfterItChanges` and
`LegacyWorkerRechecksCaseListAfterItChanges` then passed 2/2 with actual
workers, the real `ellai_state` queue tool, disposable PostGIS and synthetic
models. Both persisted the queue options, replayed the unchanged list, and
omitted it from follow-up model requests after another case was added. Run
with the intake PostgreSQL fixture enabled and filter on
`FullyQualifiedName~WorkerRechecksCaseListAfterItChanges`.
`LiveMuseWorkerRechecksCaseListAfterItChanges` and its synthetic framework
control subsequently passed 2/2 using live Muse and disposable PostGIS with
synthetic cases. The live framework called the real queue tool and the recorded
model input excluded the earlier list after another case was added. This worker
fixture does not verify deployed Gateway behavior. Independent security review
and atomicity across the provider or persistence boundary remain **NOT VERIFIED**.

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
Focused tests cover denial and synthetic provider failures.
The shared token ledger now performs reservation arithmetic in 64 bits and
saturates its existing 32-bit totals rather than wrapping for very large
provider usage. Both in-memory and PostgreSQL hydration saturate the same
way. Focused ledger/framework tests passed 45/45, and a disposable PostgreSQL
fixture verified large persisted samples remain over budget after hydration.
Framework model calls now hold in-process token and conservative spend
reservations until their provider call and usage recording finish, including
on failure. Concurrent framework calls sharing one ledger therefore account
for each other's estimated capacity. Existing `TryReserve` callers see those
reservations but do not create reservations of their own. This is not a durable
cross-instance reservation, and provider calls without complete usage data
still cannot establish actual billable usage. Focused ledger/framework tests
passed 49/49, including simultaneous reservations and release after provider
failure. Registration, worker and guest-route tests passed 27 with three
opt-in tests skipped.
An additional framework streaming test holds a model call open, confirms a
second call cannot reserve the same session capacity, and verifies cancellation
releases the reservation. When a non-streaming provider returns usage after the
caller cancels, the framework now records that usage with a non-cancelled token
before propagating cancellation, matching its streaming usage-write behavior.
The focused ledger and framework suite passed 48/48. An interrupted stream with
no provider usage still has unknown actual cost; the cancellation test does
not establish real-provider billing or distributed coordination.

Framework tool execution now requests cancellation after 30 seconds and rejects results
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
The uncommitted framework path now buffers provider answer text until the
decision, evidence and currency checks pass, then publishes the validated
answer through the existing response callback. A regression test previously
demonstrated that the rejected draft `Wrong.` was streamed even though repair
returned `Done.`; it now verifies only `Done.` is published. The provider still
uses framework streaming, but user-visible answer text no longer arrives
incrementally during generation: this intentional safety tradeoff requires
voice/text latency and UX acceptance before cutover. The application suite
passed 1,045 with 10 skipped; the synthetic worker/recorded-voice tests passed
3/3, and `LiveFrameworkRunnerStreamsBrokeredToolDecision` passed 1/1 with the
configured live Muse route. These checks do not prove live browser or device
streaming. After buffering was added, `RecordedVoiceUsesLiveMuseAndSpeech` and
`SyntheticSpeechFlowsThroughLiveTranscriptionMuseAndSpeech` passed together
2/2 with live Muse, local Kokoro, synthetic speech, and live transcription in
the latter test. They used an in-memory session and a disposable loopback relay;
browser/device playback and time-to-first-audio were not measured. Run with
`ELLAI_LIVE=1`, `SWIFTAID_TEST_LIVE_SPEECH=1`, `SWIFTAID_TEST_LIVE_ASR=1`,
configured provider settings and the loopback speech fixture. Live Muse
correction behavior, full process recovery,
and complete multi-turn parity remain cutover acceptance requirements.
The same two voice checks passed 2/2 again after removal of the production
legacy loop and addition of the framework tool-name guard. The rerun used the
configured live Muse and transcription routes, local Kokoro, an in-memory
session and synthetic audio through a temporary loopback relay. The relay and
its task-created script were removed. Browser/device playback, microphone
capture, durable session recovery and time-to-first-audio remain **NOT
VERIFIED** by this run.
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
On the current source, all four `EllaiOwnedCaseCountPostgresTests` passed with
the disposable PostgreSQL fixture and configured live Muse enabled. This
included the live Muse count, grant-expiry and unchanged-count membership-swap
checks. The filtered command was `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~EllaiOwnedCaseCountPostgresTests --logger
'console;verbosity=minimal'`. The database contained synthetic records only;
the temporary server was removed. General analytics and deployed Gateway
behavior are not established by this run.

For multi-turn replay, newly produced count results now retain their lookback
and a SHA-256 fingerprint of the authorized case IDs in internal provenance;
the model-visible tool result does not include it. The history checker reruns
the existing actor-scoped aggregate and retains the exchange only when access
is allowed, the result is complete and its membership matches. Changed
membership, denied access, outage, truncation and older traces without this
provenance are omitted. This recheck can perform up to the existing aggregate
query's authorization work at each execution fence; performance and atomicity
have not been established. Focused application history, framework and trace
checks passed 74/74, and the synthetic in-memory framework worker passed 2/2.
Those focused aggregate replay tests used a synthetic query; PostgreSQL
same-count membership coverage is recorded below. Live Muse and deployed
Gateway remain **NOT VERIFIED** for this replay path.
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
and publication are not atomic; a grant can still change after the recheck.
The model may already have received
the earlier authorized observation. Live Gateway and voice behavior remain
**NOT VERIFIED**.
The recheck now also compares the internal membership fingerprint, withholding
the answer when membership changes despite an unchanged count or when the
fingerprint is missing. Focused framework and authorized-history tests passed
71/71, including synthetic changed-membership and missing-provenance cases.
The complete application suite then passed 1,056 tests with 10 live probes
skipped using `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj
--no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'`.
`FrameworkWithholdsUnchangedCountWhenPostgresMembershipChanges` exercised the
new check against a dedicated disposable PostGIS database: one synthetic owned
case was exchanged for another while the authorized count stayed at one. The
framework withheld the answer, and the history checker retained the prior count
before the swap and omitted it afterward. This test and the two existing
PostgreSQL count tests passed 3/3 using `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
'FullyQualifiedName~EllaiOwnedCaseCountPostgresTests&FullyQualifiedName!~LiveMuse'
--logger 'console;verbosity=minimal'` with the dedicated framework fixture
enabled. The task-owned container was stopped and removed. Live Muse, deployed
Gateway and atomicity across the final recheck remain **NOT VERIFIED**.
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

Earlier in the migration, an opt-in Gateway execution selection was tested;
the current uncommitted source now registers framework execution only and
rejects legacy selection. Invalid selection and unconfigured or unsupported
framework provider routes fail closed. After that change, the
registration and worker fixtures passed 6/6
using `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
'FullyQualifiedName~EllaiReasoningRegistrationTests|FullyQualifiedName~WorkerRunsFrameworkExecutorAndPersistsAndStreamsAnswer'
--verbosity quiet`. The worker test uses a synthetic model and in-memory stores,
not a running Gateway. The obsolete loop is no longer registered as a Gateway
executor; its source has since moved to test-only fixtures for legacy regression coverage;
the model-guidance planner still uses the completion-client interface. The
complete application suite passed 1,044 tests with 10 skipped using
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj
--no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'`.
Production framework-controlled execution, provider compatibility, acceptance
tests and deployment are **NOT VERIFIED**.

The broker's server-clock result now carries an internal `server-clock/1`
provenance marker. The framework history checker can replay a successful,
non-truncated clock exchange with that marker, including through flattened
history; old unmarked or unknown-version clock traces remain unavailable.
The prompt treats prior readings as historical. Focused application history,
trace, framework and clock checks passed 69/69. The in-memory framework worker
fixture passed 2/2 after stopping its first worker and starting a new worker
against the same store; the second model request contained the first exchange
both with and without a brokered clock call. The clock case also checked the
persisted trace marker. Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~WorkerRunsFrameworkExecutorAndPersistsAndStreamsAnswer
--logger 'console;verbosity=minimal'`. This uses an in-memory store and a
synthetic model; it does not establish process restart, PostgreSQL recovery,
live multi-turn parity or deployed session recovery.

For UI guidance continuity, successful non-truncated catalogue reads with no
goal filter or with an exact server-owned descriptor goal tag record only the
resolved feature, result limit, optional registered tag and result fingerprint
in internal provenance. Framework history replay re-invokes the
existing scoped broker and retains the exchange only for the same actor,
session and unchanged authorized result. Changed catalogue content, denied
tool admission, an unavailable broker, unrecognized free-form goal queries and
older traces without this provenance remain omitted. The model-visible tool result does not
contain the replay metadata. Focused history, UI-tool and trace tests passed
62/62; the full application suite passed 1,061 with 10 live probes skipped.
After the actor-identity guard, the framework worker fixture passed 4/4 with a
synthetic model and in-memory store: after worker recreation, an unchanged
catalogue exchange reached the next model call, while a changed catalogue was
omitted. The fixture also covers ordinary and clock-tool turns. Command:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~WorkerRunsFrameworkExecutorAndPersistsAndStreamsAnswer
--logger 'console;verbosity=normal'`. These tests do not establish a deployed
UI guidance journey, real authorization revocation or replay latency.
After adding registered goal tags, focused application checks passed 64/64 and
the framework worker fixture passed 5/5, including a goal-filtered follow-up
after worker recreation. Unrecognized goal text was not persisted in the
synthetic trace. These results have the same in-memory and deployment limits.

The framework tool guard now treats a denied tool result with a blank, null,
or `Ok` reason as unavailable, preventing an absent failure code from leaving
the run unfenced. The focused guard cases passed 3/3, and the framework contract
suite passed 39/39 with `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj
--no-restore -m:1 -nr:false --filter FullyQualifiedName~EllaiAgentFrameworkContractTests
--logger 'console;verbosity=minimal'`. These are in-process tests, not a live
service-outage or deployed Gateway check.

A source review found that a provider `FunctionCallContent` with an empty name
was not explicitly rejected. A synthetic non-streaming test initially reached
the iteration limit instead of reporting an unregistered tool. The framework
guard now rejects blank names immediately; the framework contract suite passed
43/43, including non-streaming and streaming malformed-call cases. No broker
call occurred in those cases. This is not a live Muse fragmented-tool-call
compatibility check, which remains **NOT VERIFIED** after this change.

Synthetic HTTP/SSE checks through the actual configured official chat adapter
and framework now cover fragmented tool arguments: a complete tool name with
arguments split across chunks invoked the intended tool once and preserved its
call ID. A deliberately split function name (`ellai_` then `now`) exposed an
adapter limitation: this package produced the first fragment as the function
name, and the framework rejected it as unregistered without invoking a tool.
The suite passed 7/7 using `dotnet test
tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore
-m:1 -nr:false --filter
'FullyQualifiedName~EllaiMuseChatClientTests|FullyQualifiedName~FrameworkRejectsProviderToolCallWithoutAName'
--logger 'console;verbosity=normal'`. This is a synthetic protocol fixture;
whether live Muse emits split function names remains **NOT VERIFIED**. The
migration does not add a custom function-call reassembly pipeline.

After the blank-name guard change, `LiveFrameworkRunnerStreamsBrokeredToolDecision`
passed 1/1 against the configured live Muse route with a synthetic prompt,
the real brokered clock tool and an in-memory usage store. The run set
`ELLAI_LIVE=1`, loaded the existing Gateway Ellai settings into the test
process without printing them, and used `dotnet test
tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore
-m:1 -nr:false --filter
FullyQualifiedName~LiveFrameworkRunnerStreamsBrokeredToolDecision --logger
'console;verbosity=minimal'`. This one successful streaming exchange does not
prove Muse never emits split function names, deployed Gateway behavior, or
incremental user-visible text.

With the current framework-only source, the live Muse HTTP multi-turn and
test-host-restart checks passed 2/2 against a task-owned PostGIS database:
`GuestHttpFrameworkUsesLiveMuseAndDurableMultiTurnHistory` and
`GuestHttpLiveMuseRecoversConversationAfterTestHostRestart`. They used
synthetic conversation, ASP.NET Core TestServer and durable PostgreSQL state;
the latter recreates the host in the same test process. Command: `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
'FullyQualifiedName~GuestHttpFrameworkUsesLiveMuseAndDurableMultiTurnHistory|FullyQualifiedName~GuestHttpLiveMuseRecoversConversationAfterTestHostRestart'
--logger 'console;verbosity=minimal'`, with `ELLAI_LIVE=1`, the dedicated
PostgreSQL fixture opt-in, and existing Gateway Ellai settings passed into the
test process without printing them. The disposable database container was
stopped and removed. This does not prove a deployed Gateway process restart,
browser or device behavior, or general multi-turn parity for every tool.

`FrameworkWorkerRetriesProviderOutageAndCompletesTheSameTurn` passed 1/1 using
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~FrameworkWorkerRetriesProviderOutageAndCompletesTheSameTurn
--logger 'console;verbosity=normal'`. The synthetic streaming provider fails
once, then the actual worker retries the same in-memory outbox turn and
commits one answer on attempt two. This does not test a live Muse outage,
PostgreSQL retry recovery, process restart, or exactly-once provider billing.

The framework function adapter now passes the framework invocation's call ID
into the existing `EllaiToolRequest.CallId` field. Focused streaming and
dependent-tool tests passed 3/3 using `dotnet test
tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore
-m:1 -nr:false --filter
'FullyQualifiedName~FrameworkStreamingInvokesToolBeforeFinalDecision|FullyQualifiedName~FrameworkReasonsAcrossDependentToolCallsAndRejectsUnobservedEvidence'
--logger 'console;verbosity=minimal'`. These synthetic tests check the IDs
received by the broker delegate; they do not establish a persistent audit
record or deployed Gateway correlation.

Shared execution limits, request contracts and provenance types now live in
`EllaiExecutionLimits.cs`, `EllaiReasoningContracts.cs` and
`EllaiExecutionProvenance.cs`, independently of the legacy runner classes.
The active framework source no longer references `EllaiCycleRunner` or
`EllaiReasoningLoop`. Both legacy orchestration classes are now excluded from
production source and compiled only as fixtures under `tests/Shared/EllaiLegacy`.
The production prompt builder uses only the framework tool protocol. Shared
execution limits and the Gateway contract are unchanged. The application suite
passed 1,063 with 10 live probes skipped, and focused registration and worker
tests passed 12/12 after their removal from production. This is source and test
evidence, not a deployed cutover.

The worker's completed, invalid-output and superseded-turn outcome tests now
run through `EllaiFrameworkExecutor` instead of the legacy loop. Those three
cases and the two framework worker recovery cases passed 5/5 with `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
'FullyQualifiedName~WorkerAcknowledgesOnlyWithDurableExplicitOutcome|FullyQualifiedName~WorkerRunsFrameworkExecutorAndPersistsAndStreamsAnswer'
--logger 'console;verbosity=normal'`. They use a synthetic model and in-memory
store; live Gateway and database outcomes are not established by these tests.

**Provider usage gap:** `EllaiFrameworkUsage.RecordAsync` maps absent input or
output token counts to zero. `ModelCallGuard` releases its in-process budget
reservation when the call ends, so a provider response or completed stream
without usage counts does not retain the estimated tokens against the session
budget. Interrupted streams without usage details are not recorded either.
The actual usage is unknown, not proven to be zero. Budget enforcement under
missing usage and the corresponding durable accounting remain cutover gates;
an estimated fallback must be distinguishable from provider-reported usage.

The uncommitted implementation now stores missing-count estimates in separate
columns and includes them in budget totals, including for interrupted calls.
Reported spend excludes estimates; budget spend includes them. A disposable
PostGIS 17 database applied all migrations including `20261103200000_EllaiEstimatedUsage`,
and the focused PostgreSQL usage recovery and saturation tests passed 2/2.
The estimate is a budget precaution, not a verified provider token count or
invoice. Cross-process reservation atomicity and production migration remain
unverified. The temporary database container used tmpfs and was removed.
After these changes, `LiveFrameworkRunnerStreamsBrokeredToolDecision` passed
1/1 against the configured Muse route with a synthetic prompt and the real
brokered clock tool, using an in-memory usage store. This validates one live
framework exchange, not deployed Gateway behavior, PostgreSQL accounting under
live provider failure, or incremental user-visible streaming. The PostgreSQL
constraint and recovery tests also passed 2/2 on a fresh disposable database;
negative estimates were rejected and the container was removed.

The focused framework stream, repaired-decision, and verified-count tests
passed 3/3 after the recent migration changes. The current implementation still
publishes a validated decision as one text callback after model completion;
these tests do not establish incremental provider-to-user latency. The existing
response store trims each published chunk and rejects whitespace-only chunks,
so splitting arbitrary provider tokens into separate callbacks would not
preserve their exact text. Audio preparation starts from published responses.
The Blazor guidance host also inserts a space between non-final response chunks
and schedules each nonempty chunk for speech. Consequently, forwarding raw
token fragments would alter words and create inappropriate audio segments;
the existing feed expects meaningful text chunks rather than arbitrary tokens.
An incremental design must preserve the text and speech contracts while
preventing provisional or unverified model output from reaching clients.

`LiveMuseExecutorUsesAuthorizedCountForOrdinaryQuestion` exercised the actual
framework executor and production prompt with a plain-language count question,
live Muse, synthetic records, and disposable PostGIS. Two initial runs failed
with `InvalidDecision`; the second observed one count invocation, four model
calls, and repair. The executor now constructs the already server-authored count
decision directly after a single successful count observation and its existing
authorized recheck, even if model final JSON is malformed. Mixed-tool requests
still fail closed. The framework contract suite passed 48/48 and the live test
passed 1/1 after that change. A single passing live run does not establish
reliability, general aggregate support, or deployed Gateway behavior. The
disposable database container was removed.

The framework now bounds provider response text at 512,000 characters before
parsing or publication, including text accumulated inside streaming model
calls, and retains the model-call count when execution or repair fails. The
focused application framework contract suite passed 46/46 with `dotnet test
tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore
-m:1 -nr:false --filter FullyQualifiedName~EllaiAgentFrameworkContractTests`.
This synthetic test evidence does not establish live provider limits.

At the user's request, fixed per-turn iteration, tool-call, and repair ceilings
have been removed. APIs requiring an integer maximum use `int.MaxValue` as a
representation ceiling. Session token/spend budgets, authorization and
cancellation remain in force. The official framework path completed 13 tool
calls across 14 model calls in a synthetic test, and a separate synthetic
4,001-tool batch completed with an explicitly enlarged test token budget. That
batch initially exhausted the normal token budget, which remains effective.
The original 256-observation bound has been removed; the existing 60 KB
serialized provenance bound remains. Turns exceeding that byte bound cannot
be replayed as authorized history. The framework
executor deduplicates identical provenance before applying the byte
bound; a synthetic 4,001-call test retained repeated clock provenance,
while a distinct-evidence case failed closed rather than retaining a partial
trace. A compact version-3 trace with 300 distinct synthetic observations
also round-tripped through the existing PostgreSQL JSONB column; unauthorized
or unrecognized evidence remained excluded from replay. The corresponding
PostgreSQL tests passed 2/2 with a disposable database and synthetic data.
An application authorization test rechecked 300 distinct case references in
15 observations, then excluded the dependent answer after one grant was
removed. A separate 257-observation case exceeded the retained 60 KB byte
bound and was not serialized; removing the count bound does not guarantee that
every 257-observation case will fit.
The framework executor also packs consecutive permitted case observations
only when all non-evidence metadata, including expiry, matches. Each original
case reference remains present and is subject to current authorization on
replay. A synthetic 300-call framework test retained all 300 references in
15 observations; differing expirations were not combined and the oversized
trace failed closed. The framework and history authorization tests passed
99/99 with `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj
--no-restore --filter "FullyQualifiedName~EllaiAgentFrameworkContractTests|FullyQualifiedName~EllaiAuthorizedConversationContextTests"
-m:1 -nr:false --verbosity quiet`. This does not establish behavior for all
long sessions or remove the existing storage bound.
For multi-turn continuity, a no-argument `ellai_capabilities` result is now
replayable only when it is the broker's exact fixed, non-sensitive descriptor
and carries its new version marker. Changed, truncated, argument-bearing and
older unmarked results remain excluded. The actual broker and history checker
are covered by application tests; a synthetic framework-worker restart test
also passed alongside its existing cases (6/6). The application suite passed
1,098 with 10 live skips. This marker is not a permission grant and does not
establish capability readiness.
An attempted live guest test did not invoke the capability tool: the guest
public-help allowlist excludes it. The test expectation was removed rather
than expanding guest permissions. Capability replay is currently established
by the authorized synthetic broker and worker tests. A separate live Muse
framework probe then passed 1/1 with an explicitly authorized synthetic
capability tool, a versioned observation, history reconstruction and a second
model turn. It uses in-memory history and does not establish deployed or guest
capability access.
No-argument receipt-list observations now retain a SHA-256 fingerprint of
the bounded broker result alongside its existing receipt evidence references,
without storing the result summary in the trace. They are replayed only after the same actor and
session re-read the list through the authorized broker and obtain the same
result. Changed status or membership, denial, owner mismatch and broker outage
exclude the prior answer; older unmarked traces remain excluded. Tests passed for these cases and for a synthetic framework-worker
restart (7/7 worker cases); the application suite passed 1,100 with 11 live
skips. A disposable migrated PostgreSQL receipt-authority test passed 1/1:
it retained the original answer, then omitted it after the receipt status
changed; the fixture container was removed. Receipt replay has not been
independently security-reviewed or tested in a deployed Gateway.
Receipt replay now also compares every stored evidence reference with the
current broker result, including when a fingerprint was already checked in
the same history pass. A mismatched-reference test is rejected. The focused
authorization/trace tests passed 48/48; the application suite passed 1,100
with 11 skips; the PostgreSQL receipt and framework worker tests passed 8/8
against a fresh disposable database, which was removed.
Canonical individual receipt lookups are also replayed only after the same
authorized broker returns the same bounded result and evidence. Unknown or
noncanonical input is not retained as a lookup ID. Focused receipt tests passed
48/48, and a disposable PostgreSQL test passed 1/1 with separate conversation
turns for list and individual lookup replay before and after a receipt status
change. The disposable
container was stopped. Independent security review and deployed verification
remain outstanding.
Live long-running
reasoning and behavior beyond the bounded history trace are not verified.

`FrameworkWorkerPublishesOnlyValidatedStreamAndPreparesGatewayAudio` passed
1/1 with a held synthetic streaming model, in-memory session store, actual
framework executor and worker, prepared audio service, Gateway audio handler,
and synthetic PCM provider. It checks that the incomplete answer is withheld
until validation and that the validated answer can be retrieved as audio.
Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj
--no-restore --filter FullyQualifiedName~FrameworkWorkerPublishesOnlyValidatedStreamAndPreparesGatewayAudio
-m:1 -nr:false -p:WasmBuildNative=false --verbosity quiet`. An initial build
without `-p:WasmBuildNative=false` failed because `emcc` was unavailable.
This test does not demonstrate incremental answer publication, a live speech
provider, a browser, or a deployed Gateway.
The worker outcome and prepared audio suites passed 23/23 using the same
project and build property with `--no-build --no-restore --filter
"FullyQualifiedName~EllaiUtteranceWorkerOutcomeTests|FullyQualifiedName~EllaiPreparedResponseAudioTests"`.

After the complete answer and its evidence pass validation, the framework
executor now publishes longer multi-sentence answers in chunks suitable for
the existing response feed and speech preparation. It checks the session
fence before each chunk and splits only where the feed's space-joining
preserves the original text. This is progressive delivery of a validated
answer, not publication while the model is still generating. The focused
framework suite passed 61/61 and the full application suite passed 1,095 with
10 skipped; worker and audio handler tests passed 6/6 with a synthetic model
and speech using a short single-chunk answer. Subsequently, the held-stream
worker/audio handler test passed 2/2 for single- and multi-chunk answers.
It checks chunk order, turn correlation, text reconstruction, and synthetic
PCM retrieval for every chunk after validation. The live speech provider,
deployed Gateway, and device behavior remain unverified for this change.
Cancellation signalled inside a text publication callback now stops the turn
immediately, including after its final chunk. Focused synthetic framework tests
passed 63/63 and the application suite passed 1,097 with 10 skipped after
this change. A chunk delivered before cancellation cannot be retracted.

The Gateway worker now holds validated answer chunks until the outbox's
attempt-fenced durable completion succeeds. It publishes chunks only when
their reconstructed text matches the committed answer. A synthetic in-memory
worker test reclaimed an expired claim while the first model response was held;
the stale attempt published no answer and could not commit. Another held
completion while the session epoch changed and confirmed that cancellation
prevented answer publication. These cases and single- and multi-chunk delivery
passed 4/4. PostgreSQL and multiple Gateway processes were not tested for
this change. Transient publication after a successful commit is not atomic
with a subsequent turn or cancellation.

Long-running workers now renew the existing PostgreSQL outbox lease every two
minutes while the attempt remains current and unexpired. Failed renewal
cancels the attempt; a crashed or disconnected worker remains recoverable on
lease expiry. An in-memory worker cancellation test and lease-fencing tests
for in-memory and disposable PostgreSQL stores passed 3/3. An initial
`MigrateAsync` run reported pending model changes because the dedicated test
context omitted `UseOpenIddict()`, which both the Gateway migration runner
and the design-time factory use. After aligning the test context, the
disposable PostgreSQL database migrated successfully and lease fencing,
long-history worker recovery, and trace round-trip tests passed 3/3. The
fixture used two database contexts, not multiple deployed Gateway processes;
the task-created tmpfs container was removed. Deployed migration remains
unverified.
The worker also checks cancellation after its execution returns, so a
noncooperative executor cannot commit a late answer after lease renewal fails.
The synthetic worker test passed 3/3 for renewal success, cooperative
cancellation, and a test executor that returns after ignoring cancellation.
Noncooperative execution can still hold the worker until it returns.
Lease renewal now also remains active through the attempt-fenced durable
commit. Synthetic worker tests held the commit across repeated renewals and
separately failed renewal during the hold; the latter produced a retryable
failure without answer publication. The focused framework worker tests passed
8/8. These checks use in-memory stores and do not establish database network
partition behavior or exactly-once execution.
`FrameworkWorkerRenewsPostgresClaimWhileModelIsRunning` subsequently passed
1/1 against a migrated disposable PostgreSQL database with the actual
framework worker, independent scoped database contexts, and a held synthetic
model. It observed the lease extension, rejected a competing claim at the
original expiry, and completed the first attempt. Command: `dotnet test
tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore
-m:1 -nr:false -p:WasmBuildNative=false --filter
FullyQualifiedName~FrameworkWorkerRenewsPostgresClaimWhileModelIsRunning`,
with the dedicated loopback fixture enabled. The task-created tmpfs container
was removed. This does not test a provider outage, network partition, or
multiple deployed Gateway processes.
If transient response or audio publication throws after the durable outbox
commit, the worker now logs the delivery failure without marking the
completed turn for retry. A synthetic audio failure test verified the
completed answer remained recoverable and no outbox failure was recorded;
the worker and prepared-audio suites passed 32/32. This does not establish
live speech-provider recovery or delivery after a process crash.

`FrameworkWorkerRestoresHistoryAfterManyRepeatedToolObservations` passed 1/1
with the actual worker, synthetic model, and a disposable migrated PostgreSQL
database. The first turn returned 300 clock tool results, stored one equivalent
provenance observation, and a restarted worker included the first exchange in
the next model request. This exercises repeated identical provenance, not 300
distinct private observations, a live model, or a Gateway process restart.
The temporary PostgreSQL container was removed.

After removal of the fixed call ceilings and the current provenance changes,
the live Muse guest HTTP multi-turn and test-host restart tests passed 2/2
against a disposable migrated PostgreSQL database (total duration 2m 9s).
They use synthetic guest text and in-process test hosts; they do not establish
deployed Gateway restart, long-running live tool sequences, or device behavior.
The task-created database container was removed.

After the execution-limit and provenance changes, the framework's live exact
Qdrant recall/current-access test and vectorless scope-isolation test passed
2/2 using a disposable loopback Qdrant with synthetic data. The supporting
application tests for changed scope, stale or unavailable index, and owner or
snapshot rejection passed 10/10. A disposable Redis instance passed its cache
prefix/expiry/removal test, and the stopped instance passed explicit read,
write and removal failure handling (1/1 each). These Redis checks exercise the
existing cache service, not an Ellai session store. Both task-created
containers used tmpfs and were removed. These checks do not prove deployed
Gateway behavior or live PostgreSQL authority in this particular rerun.

The current working tree's `EllaiAgentFrameworkContractTests` passed 75/75
with `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj
--no-restore -m:1 -nr:false --filter FullyQualifiedName~EllaiAgentFrameworkContractTests`.
These application-level tests include cooperative cancellation and rejection of
late results; they do not establish a hard stop for a noncooperative tool or
incremental publication of unvalidated model text.

The focused service-registration regression rerun on the current working tree
returned 17 passed and 4 failed (`dotnet test tests/SwiftAid.Integration.Tests/
SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false --filter
"FullyQualifiedName~CaseCommandProcessorDependencyInjectionTests|FullyQualifiedName~ProtectedPayloadKeyRingSecurityTests"`,
with `OS=Windows_NT`). All four failures occur while validating the session
reminder delivery graph: `SessionReminderChannelSender` requires an unregistered
`UserManager<SwiftAidUser>`. The sender, its registration, and both test files
match the current Git HEAD, and the Gateway migration diff does not modify
their registration. A clean baseline test run was not performed; this evidence
does not prove the failures occur on baseline. The regression gate remains open.
An initial rerun without `OS=Windows_NT` failed during the WebAssembly build
because `emcc` could not be found; no tests ran in that attempt.

After the response-generation fence change, a separate local Gateway process
using the current Debug build, a freshly migrated disposable PostgreSQL
database and live Muse passed `node tests/SwiftAid.Ui.Tests/ellai-cancel.gateway.browser.cjs`:
the synthetic guest turn reached processing, was cancelled, and produced no
committed answer or response chunk. PostgreSQL recorded one usage sample.
The same fixture passed `node tests/SwiftAid.Ui.Tests/ellai-text.gateway.browser.cjs`:
the answer completed and was recovered with the owner cookie after reload.
The first answer text arrived at 12,142 ms, at completion, so model-time text
streaming is still not established. Both tests used headless Chromium and the
live-provider opt-in against `http://127.0.0.1:18246`; the candidate returned
static-asset HTTP 500 errors, so these are API/feed checks, not rendered UI
acceptance. The Gateway process and disposable database container were stopped.
The synthetic certificate and key directory remain in the previously retained
task fixture directory.

The current source was then published with its web assets and started as a
separate loopback Gateway against a newly migrated disposable PostgreSQL
database and live Muse. `node tests/SwiftAid.Ui.Tests/ellai-conversation.browser.cjs`
passed a synthetic two-turn rendered Chromium journey: the first answer was
restored after refresh, the follow-up recalled the synthetic word, the mobile
thinking indicator and reduced-motion behavior were checked, and no page
errors were reported. A separate Chromium network check found no HTTP errors
on the initial page load. The first observed text was at 13,831 ms and the
completed outcome at 13,871 ms; this is not proof of model-time streaming.
The native publish emitted SQLite WASM0001 warnings. The Gateway and database
container were stopped; the publish remains in the previously retained task
fixture directory. No deployed Gateway or physical device was tested.

The response feed now hides transient responses for a durably cancelled
session, and both guest and authenticated audio routes reject new requests
for that session. The worker also rechecks the durable session before
publishing a completed answer. The focused authorization and worker suites
passed 51 tests with 3 opt-in tests skipped. This checks HTTP routes and a
simulated remote epoch change; it does not prove atomic publication across
processes or stop an audio stream already in flight on another instance.

`GuestCancellationOnAnotherHostHidesLocalResponseAndAudio` passed against a
fresh disposable PostgreSQL database and two simultaneously running in-process
Gateway test hosts with separate response stores. A guest session and stale
synthetic chunk existed on the first host; cancellation on the second host
left that local chunk present, while the first host's HTTP feed suppressed it
and its audio route returned 404. The database container was removed. This
does not test separate OS processes or an already-open audio stream.

The audio route's second session read also rechecks ownership. A focused HTTP
test simulated an owner change between the group authorization filter and the
audio filter and received 404. The related cancellation and active/completed
compatibility tests passed 4/4; the two-host PostgreSQL cancellation test
passed again after this change. The disposable database container was removed.

Open response-audio streams now recheck the existing durable session at the
start of streaming and approximately once per second. Cancellation, owner,
scope or epoch changes interrupt that response's audio preparation and abort
the stream; a failed authority read also stops the stream. This adds database
reads and makes authority outages stop active audio, justified by SA-ELV-04's
requirement to stop playback on cancellation or revoked authority. It does not
change the session store or introduce a new coordination service. A disposable
PostgreSQL test with two in-process Gateway hosts passed: after the first host
started synthetic PCM, cancellation on the second host stopped preparation and
the open stream, suppressed its local stale feed, and rejected later audio
requests. The focused speech suites passed 19/19, including an injected
authority outage. Timing depends on database and transport responsiveness;
separate OS processes, physical playback and provider outages are unverified.

After the audio session-fence change, the current source was published and
tested against live Muse, a fresh disposable PostgreSQL database and the local
Kokoro service. `node tests/SwiftAid.Ui.Tests/ellai-sentence-audio.gateway.browser.cjs`
passed: three validated sentence chunks each returned HTTP 200 with complete
24 kHz `af_heart` PCM. First text was observed at 16,021 ms; this was after
model generation, so model-time incremental streaming remains unverified.
This exercised the HTTP audio path in headless Chromium, not physical playback.
The separate candidate Gateway, disposable database and task-created loopback
relay were stopped; the published candidate remains in the retained task fixture.

Cancellation during the final model-admission callback previously allowed a
provider dispatch in both streaming and non-streaming modes. Both paths now
check cancellation after admission and before dispatch; cancellation also wins
over an admission denial. Four focused synthetic cases verify that no provider
call or usage sample is recorded, and the framework and usage suites passed
86/86. This closes that dispatch race, not the hard-stop gap for a provider or
tool that is already running and ignores cancellation.

The framework tool guard now preserves cancellation when an authority check
returns a denial or a dependency throws after cancellation; it does not start
the tool after a cancelled pre-check and does not accept a result after a
cancelled post-check. The focused framework and usage suites passed 92/92.
The live Muse framework streaming/brokered-tool and streaming cancellation
probes also passed 2/2 with synthetic text. These checks do not prove a hard
stop for already-running noncooperative operations.

Decision and publication authority checks now preserve cancellation rather
than report a denial when cancellation occurs during the check. The focused
framework/usage suites passed 94/94, and the live Muse brokered-tool and
streaming-cancellation probes passed 2/2 after this change. These checks do
not establish model-time incremental output or a hard stop for a dependency
that ignores cancellation.

A current live Muse aggregation run exposed a repeatable provider output-limit
failure after the authorized count tool had already returned. For a single
allowed count call, the framework's function-loop termination now avoids an
unnecessary second model completion; the existing fresh authorized count and
membership recheck still constructs the final answer. Mixed-tool calls remain
subject to the existing rejection. After the change, all 5 focused PostgreSQL
count tests passed, including 2 live Muse cases, grant expiry and changed
membership; the framework/usage suites passed 94/94. The disposable PostgreSQL
container was removed. This establishes the tested owned-case count, not
arbitrary analytics or deployed Gateway acceptance.

The opt-in `LiveMuseWorkerRecallsAuthorizedCaseFromPostgresAndQdrant`
integration test passed 1/1 on 2026-11-03 with the configured live Muse route,
a synthetic case, disposable loopback PostgreSQL and Qdrant, and the framework
worker. Across three turns it verified authorized retrieval, a follow-up while
Qdrant requests failed, and omission of the case identifier after the grant
expired. Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~LiveMuseWorkerRecallsAuthorizedCaseFromPostgresAndQdrant' --logger 'console;verbosity=normal'`.
Both fixture containers stopped after the test. This is a worker integration
test, not a deployed Gateway, browser, or incremental streaming acceptance test.

On 2026-11-03 `GuestHttpFrameworkUsesLiveMuseAndDurableMultiTurnHistory`
passed **1/1** against the current source, real Muse and a disposable PostgreSQL
database. The in-process Gateway test exercised guest antiforgery and session
creation, four sequential turns, a framework product-facts tool call, response
feed entries, durable conversation history and usage. Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~GuestHttpFrameworkUsesLiveMuseAndDurableMultiTurnHistory' --logger 'console;verbosity=minimal'`.
The fixture container stopped after the test. This does not verify a deployed
Gateway, physical device or incremental model-time streaming.

The four-turn live Muse guest HTTP test was rerun after the advisory-threshold
and isolated-tool changes with a zero spending threshold and priced input. It
passed **1/1**, including the tool and retained-history checks. The test used
the same disposable PostgreSQL and in-process Gateway fixture; it is not a
deployed Gateway test. The database container was stopped.

The current Gateway authorization and recovery class passed **27**, failed
**0**, and skipped **2** live Muse cases with a disposable framework PostgreSQL
fixture: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiPrivilegedRouteAuthorizationTests' --logger 'console;verbosity=quiet'`.
The live Muse host-restart case was then enabled separately and passed **1/1**
with `--no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~GuestHttpLiveMuseRecoversConversationAfterTestHostRestart' --logger 'console;verbosity=minimal'`.
These are in-process test hosts, not independent deployed Gateway processes;
both disposable databases were stopped.

The utterance runner checks cancellation after its publication authority read
and before entering the text callback. A synthetic authority store returning
after cancellation verifies that the callback is not entered. The runner and
framework contract filters passed **105/105** with `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~EllaiUtteranceRunnerTests|FullyQualifiedName~EllaiAgentFrameworkContractTests' --logger 'console;verbosity=minimal'`.
This closes that publication race; it does not establish model-time streaming
or forcibly stop a noncooperative dependency.

With `ELLAI_LIVE=1` and the configured live provider, the application
`EllaiLiveProbeTests` run on 2026-11-03 passed **12/13** and failed the
capability-observation follow-up. The failed follow-up received a provider
`Length` finish reason at the test's 1,024-token limit; a separate rerun failed
the same way. The Gateway uses 2,048 tokens, but these runs do not establish
that this follow-up succeeds at that limit. Command: `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~EllaiLiveProbeTests' --logger 'console;verbosity=normal'`.
Live synthetic ASR upload and realtime streaming passed in this run. The
follow-up failure remains open and the provider's truncated output was not
published as a valid decision.

A separate live probe of the same authorized capability follow-up at the
Gateway's 2,048-token per-response setting passed **1/1** on 2026-11-03:
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~LiveFrameworkPreservesCapabilityObservationAtGatewayTokenLimit' --logger 'console;verbosity=normal'`.
The original 1,024-token test remains unchanged and its failure remains open;
one passing provider run does not establish reliability across all prompts.

A live Muse probe on 2026-11-03 sent the full generated Ellai decision schema
with a native synthetic function call. Muse accepted the schema and returned
the tool call; its first final decision used schema version 1 and failed the
existing validator. With an explicit version-2 instruction, the same probe
passed **1/1**. Command: `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~LiveMuseSupportsFullDecisionSchemaWithNativeToolCall' --logger 'console;verbosity=normal'`.
This does not establish all decision variants, streaming schema compatibility,
or compatibility with other configured providers. Production response format
was not changed based on this single probe.

An additional live framework-streaming probe with the full schema and a
brokered clock tool passed once but failed on rerun when a repair completion
reached the provider output limit. Production schema-format wiring was removed
after that failure. The existing framework/usage contract filter passed
**94/94** afterward. The provider schema format is therefore still an open
compatibility issue, not a validated fix for incremental output or repair.

At the user's request, the Gateway treats cumulative session-token and spend
budgets as logged warning thresholds rather than admission blockers; usage and
estimated in-flight reservations remain recorded. The legacy signed-in turn
count is also a warning threshold, including for previously created sessions.
The configured per-response provider token limit and tool execution limits are
still enforced. A disposable PostgreSQL check passed for a follow-up beyond
the turn threshold and rejection of a stale version. Focused usage/session
tests passed **33/33**, and database/registration tests passed **10/10**.
The application suite passed **1,156**, failed **0**, skipped **13**; the
integration suite passed **744**, failed **0**, skipped **304**. These tests do
not establish deployed configuration or that the earlier live 1,024-token
provider output-limit failure is fixed. Warnings are Gateway logs, not client
notifications; exceeding spend thresholds can incur additional provider cost.

At the user's request to support multi-turn reasoning, the fixed selection cap
of six prior exchanges and the 12,000-character selection cutoff have been
removed. The execution-history read no longer applies its former 100-row
cutoff. Existing provenance and current-authorization checks still apply.
Model context windows remain provider limits; a larger request can still be
rejected by the provider and may cost more.
Focused authorized-history, outbox and framework tests passed **145/145**;
disposable PostgreSQL tests for twelve short retained turns, owner isolation,
and the queued-turn anchor passed **2/2**. The database container was stopped.
The twelve-turn case was subsequently extended through the Gateway framework
worker with a synthetic model: it verified both the oldest and newest exchange
reach the model, the follow-up completes, and its provenance trace is readable.
It passed **1/1** with `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~FrameworkHistoryRetainsTwelveShortOwnedTurns' --logger 'console;verbosity=normal'` against disposable PostgreSQL; the container was stopped.
After removing the character cutoff, the focused authorization, outbox and
framework application tests passed **147/147**. The synthetic and live Muse
framework-worker cases both passed **2/2** against disposable PostgreSQL with
twelve prior exchanges exceeding 12,000 characters; live Muse recalled a code
word from the first exchange. The database container was stopped. Command:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~FrameworkHistoryRetainsTwelveShortOwnedTurns|FullyQualifiedName~LiveMuseRecallsLongOwnedHistory' --logger 'console;verbosity=normal'`.
Independent security review remains unverified.

Execution-history reads in PostgreSQL and the in-memory store include the
current turn and completed earlier turns. Failed or queued prior turns are
excluded; later turns remain excluded and owner checks still apply. The focused application history/outbox
tests passed **59/59**, and disposable PostgreSQL history tests passed **3/3**,
including 105 failed earlier turns and 105 queued later turns. Commands:
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~EllaiUtteranceOutboxTests|FullyQualifiedName~EllaiAuthorizedConversationContextTests' --logger 'console;verbosity=minimal'`
and `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~FailedTurnsDoNotEvictCompletedExecutionHistory|FullyQualifiedName~ExecutionHistoryKeepsItsAnchorWhenMoreThanOneHundredLaterTurnsAreQueued|FullyQualifiedName~FrameworkHistoryRetainsTwelveShortOwnedTurns' --logger 'console;verbosity=normal'`.
The database container was stopped.

After removal of the execution read's fixed 100-row cap, the PostgreSQL-backed
framework worker received the first and last of 105 completed prior turns, and
owner isolation and queued-turn anchoring passed. PostgreSQL history checks
passed **3/3** and application history/outbox checks passed **59/59**. The
105-turn worker used a synthetic model. The database container was stopped. Unbounded history can increase
memory, authorization work, latency and provider costs and may exceed the
provider's context window; its large-session performance is not verified.

A subsequent test of 105 short prior exchanges passed **2/2** with a synthetic
model and live Muse against disposable PostgreSQL. Live Muse recalled a code
word from the first exchange. Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~FrameworkWorkerReceivesMoreThanOneHundredCompletedTurns|FullyQualifiedName~LiveMuseRecallsFirstOfMoreThanOneHundredTurns' --logger 'console;verbosity=normal'`.
The database container was stopped; this is not a deployed Gateway test.

The authorization pass now indexes completed turns for legacy provenance
dependencies and uses a set for retained-turn lookup; provenance collection
uses an explicit stack and checks cancellation. This avoids repeated full
history scans in those paths without changing reauthorization requirements.
The focused authorization, framework and outbox suite passed **147/147**:
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~EllaiAuthorizedConversationContextTests|FullyQualifiedName~EllaiAgentFrameworkContractTests|FullyQualifiedName~EllaiUtteranceOutboxTests' --logger 'console;verbosity=minimal'`.
Large-session benchmarks and independent security review remain unverified.

The execution fence now compares retained turn IDs with a set after each fresh
authorization pass, avoiding a quadratic membership scan as histories grow.
The authorization, utterance-runner and framework contract filters passed
**162/162** with `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~EllaiAuthorizedConversationContextTests|FullyQualifiedName~EllaiUtteranceRunnerTests|FullyQualifiedName~EllaiAgentFrameworkContractTests' --logger 'console;verbosity=minimal'`.
The same fresh permission checks are still performed; this is not a benchmark
or independent security review.

Current default regression runs on the working tree: application **1,165
passed, 0 failed, 16 skipped**; integration **758 passed, 0 failed, 309
skipped**. The initial application run exposed two tests still asserting the
removed six-turn and character cutoffs; their assertions were updated to
verify complete ordered history, and the application suite was rerun. Commands:
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --logger 'console;verbosity=quiet'`
and `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --logger 'console;verbosity=quiet'`.
Skipped tests are not acceptance evidence.

An opt-in integration run using live Muse, disposable PostgreSQL and Qdrant,
and a stopped disposable Redis endpoint passed **66**, failed **0**, and
skipped **12**. It covered `EllaiMemoryIndexTests`, `EllaiPostgresTests`,
`EllaiOwnedCaseCountPostgresTests`, and `EllaiPrivilegedRouteAuthorizationTests`.
Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiMemoryIndexTests|FullyQualifiedName~EllaiPostgresTests|FullyQualifiedName~EllaiOwnedCaseCountPostgresTests|FullyQualifiedName~EllaiPrivilegedRouteAuthorizationTests' --logger 'console;verbosity=quiet'`.
The task-created containers were stopped. The SQLAUTH fixture gate remained
disabled because its fixed port belongs to an existing container; this run is
not evidence for those skipped cases or a deployed Gateway.

Receipt lookup is now included in the Gateway's isolated read-only timeout
boundary. The broker uses session-scoped `GetAsync`/`ListBySessionAsync`, and
the PostgreSQL implementation uses no-tracking reads; receipt writes are not
detached. In a noncooperative synthetic-store test the timed-out turn failed,
the next turn completed before the read returned, and no late answer was
published. The isolation filter passed **14**, failed **0**, skipped **1**:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~FrameworkWorkerFinishesTimedOutTurnBeforeNoncooperativeReadReturns|FullyQualifiedName~EllaiIsolatedToolCallsTests' --logger 'console;verbosity=normal'`.
The skipped test needs disposable PostgreSQL. A timed-out read can continue in
its owned scope until it returns; this does not stop arbitrary writes or prove
timeouts for every tool.

The authorized import-destinations read is also isolated: its PostgreSQL
implementation queries current allowed workspaces and does not perform an
import. A noncooperative synthetic workspace reader timed out without blocking
the next turn; the expanded isolation filter passed **17**, failed **0**, and
skipped **1** using the same command above. The location-search tool remains
cooperative because `GeographicRegistryService.SearchAsync` can seed the
registry on first use; detaching it could allow a write after timeout.

The existing authorized import-history and import-status reads now use the
same isolated boundary. Their PostgreSQL implementations recheck workspace
authority and read import records; they do not submit or mutate imports.
Noncooperative synthetic implementations of both reads timed out without
blocking the next turn. The expanded isolation filter passed **21**, failed
**0**, skipped **1**, using the isolation command above. The skipped case
requires disposable PostgreSQL. Underlying noncooperative reads may continue
after the turn returns, and other mutating paths remain cooperative.

The UI catalogue and UI observation reads now use the isolated timeout
boundary as well. Their existing authority lookup, registry snapshot, latest
session observation and default discovery path are read operations. A
noncooperative synthetic authority lookup was tested for each: timed-out
turns failed and a subsequent turn completed before the held read returned.
The isolation filter passed **25**, failed **0**, skipped **1**, using the
same command above. Underlying reads can continue until completion; the
skipped PostgreSQL authority case remains separately gated.

The same isolation filter was subsequently run with its disposable PostgreSQL
authority fixture enabled: **26 passed, 0 failed, 0 skipped**. The database
container was stopped. Classification evidence remains cooperative: the
existing `PostgresCaseClassificationQuery` holds a case-authority read lease,
and the optional `PostgresSignalReviewService` holds leases for contributing
cases. Detaching a noncooperative operation there could retain those locks
after the turn returns; no hard-timeout claim is made for that tool.

For Gateway topic/evidence recall, the existing Qdrant exact-reference read
now runs in its own scope behind the existing bounded isolation pool. If it
ignores the existing 500 ms cancellation deadline, the caller can fall back to
the authoritative PostgreSQL topic and current case authorization while the
derived read finishes in its owned scope. The wrapper is used only when the
broker index is the scoped service; custom brokers retain their configured
index. The existing PostgreSQL/Qdrant authorized-recall and outage scenario
passed **1/1** after the fixture was registered like production. A combined
run with another PostgreSQL fixture first failed during concurrent migrations
(`lock_token` already exists); the isolated rerun passed. The same scenario
was then exercised with its Qdrant transport held beyond cancellation: the
worker completed the authorized PostgreSQL recall before the held request was
released, and the existing grant-expiry checks still passed (**1/1**). The
database and Qdrant containers were stopped. The held read remains in its owned
scope until it returns.

An attempted separate Gateway process against disposable PostgreSQL exited
before its health endpoint became available. A repeat startup diagnostic
identified an `InvalidOperationException` but did not establish the exact
cause; the HTTP guest turn did not run. The task-created database containers
and process jobs were stopped, and the existing Gateway was not restarted.
Independently hosted Gateway acceptance therefore remains open.

The startup diagnosis found that disabling Identity still registered an
account presentation client requiring `UserManager`. The Gateway now uses
the existing unavailable account client when both Identity and Federation are
disabled. With the corrected fixture settings, a separate development Gateway
process became healthy and served a live Muse guest turn over loopback HTTP,
using disposable PostgreSQL and NATS. The process and disposable containers
were stopped. The existing Gateway was not restarted. This is one real-host
guest turn, not a deployed cutover or full security acceptance.

Following the execution-history read change, the live Muse guest HTTP test and
test-host restart recovery test both passed **2/2** with disposable PostgreSQL.
The first exercised four turns, a brokered product-facts tool and retained
history; the second exercised a restarted in-process test host. Command:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~GuestHttpFrameworkUsesLiveMuseAndDurableMultiTurnHistory|FullyQualifiedName~GuestHttpLiveMuseRecoversConversationAfterTestHostRestart' --logger 'console;verbosity=normal'`.
The PostgreSQL container was stopped. The already-running Gateway container was
not rebuilt or restarted; this evidence does not establish deployed cutover.

The framework contract suite also covers a streamed function call followed by a
provider output-limit finish: the call is not executed, no answer text is
published, and usage is recorded. The full contract class passed **88/88** with
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~EllaiAgentFrameworkContractTests' --logger 'console;verbosity=minimal'`.
This validates the truncated-call boundary, not incremental answer delivery.

Non-streaming model calls now retry one output-limit response with double the
output allowance (up to 16,384), after checking current authorization again.
The incomplete response is never passed to framework function execution, and
both provider calls are recorded. Synthetic tests passed **2/2**, including a
second truncation that stops safely; the framework contract class previously
passed **89/89** with the retry. The live 1,024-token capability follow-up then
passed **1/1**, but that run does not prove the provider returned truncation or
that live behavior is stable. Streamed calls still fail safely on truncation;
the retry does not solve incremental answer streaming.

After that retry, a fresh full live Muse probe run produced **15 passed, 1
failed**. Both capability-follow-up probes passed; the realtime synthetic-tone
ASR probe failed with a WebSocket close/TLS decryption error, and a separate
rerun failed with the same error. Upload ASR passed. The earlier successful
realtime run does not establish current reliability, and this evidence does not
identify whether the provider, network, or client caused the failure. Commands:
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~EllaiLiveProbeTests' --logger 'console;verbosity=normal'`
and the same command with filter `FullyQualifiedName~LiveRealtimeAsrStreamsSyntheticTone`.
The failure occurred after the realtime handshake, while receiving provider
frames. The local voice integration class passed **18**, failed **0**, and
skipped **3** live-dependent cases using `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiVoiceTranscriptionServiceTests' --logger 'console;verbosity=minimal'`;
its loopback WebSocket fixtures do not exercise the live TLS connection.
The non-streaming truncation retry additionally passed **3/3** focused tests,
including rejection of the retry when authorization changes between calls.

The same live synthetic audio was then sent from a disposable Linux container:
the provider returned a normal WebSocket close. On Windows, default TLS
negotiation failed again; selecting TLS 1.2 for the Windows realtime WebSocket
made the live probe pass **5 consecutive runs**, including one after commit
`5b87cc6`. The local voice integration class passed **18**, failed **0**,
skipped **3** with this change. Linux keeps its existing TLS negotiation.
Windows realtime transcription now requires the endpoint to support TLS 1.2;
the exact cause of the default-negotiation failure is not proven. Real
microphone/device and deployed Gateway voice acceptance remain unverified.

The next full live probe class produced **15 passed, 1 failed**: realtime
speech and both capability-follow-up probes passed, while the experimental
official-client JSON-schema/native-tool probe returned no tool call. This
probe is not the production framework configuration; its earlier pass and this
failure show that the combined provider behavior is not consistently verified.

With the configured live Muse route, the latest complete live probe class ran
**15 passed, 1 failed, 0 skipped**. The 1,024-token capability follow-up passed
in a separate two-test run but failed with `ProviderOutputLimit` in the full
run; the 2,048-token Gateway configuration passed both runs. The 1,024-token
behavior is therefore intermittent, and two passes at 2,048 do not establish
that truncation cannot occur there. The run included synthetic upload and
realtime ASR, official client, tool-call and framework probes. Command:
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~EllaiLiveProbeTests' --logger 'console;verbosity=normal'`.

The current memory-index integration class was rerun with disposable loopback
PostgreSQL and Qdrant: **13 passed, 0 failed, 1 skipped**. This includes the
framework worker's authorized recall, current-access checks, Qdrant outage and
provider restart recovery. The skipped case requires live Muse. Command:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiMemoryIndexTests' --logger 'console;verbosity=normal'`.
Both disposable containers were stopped. This is not deployed Gateway evidence.

Advisory warnings now also fire when recorded model usage or audio cost crosses
a threshold after a call, rather than waiting for another model admission.
The focused usage/framework tests passed **107/107**, and Gateway threshold
and two-turn HTTP tests passed **11/11** after this change.

The Gateway registration test now captures the actual warning log and checks
that an over-threshold framework request still reaches the provider and
completes. The registration filter passed **9/9** with `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiReasoningRegistrationTests' --logger 'console;verbosity=minimal'`.

The in-process guest HTTP/framework-worker conversation test now also runs
with the Gateway's advisory accounting, a zero spending threshold and priced
tokens. Both turns completed and retained history. Together with registration
checks, the filter passed **11/11** using `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~GuestHttpTurnsReachFrameworkWorkerAndRetainConversation|FullyQualifiedName~EllaiReasoningRegistrationTests' --logger 'console;verbosity=minimal'`.
This uses a synthetic model and in-process host; live deployment remains
unverified.

The same cancellation fence now covers history and consent rechecks, the
final session read, and return from the reasoning executor. A synthetic store
that returns an active session after cancellation verifies that the runner
does not report a completed turn. The focused runner/framework tests passed
**106/106**; the full application suite passed **1,155**, failed **0**, and
skipped **13** using `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-build --no-restore -m:1 -nr:false --logger 'console;verbosity=quiet'`.

The live owned-case count test now also checks a framework follow-up using the
prior turn's 30-day window. Muse selected the same window, the broker executed
and rechecked the authorized count, and the answer matched PostgreSQL. After
the grant expired, history authorization omitted the earlier count. This
passed with live Muse and disposable PostgreSQL at the Gateway's 2,048-token
turn setting. An earlier exploratory follow-up asking for the prior count
without a tool hit the provider output limit; that request conflicted with the
existing prompt rule requiring fresh case-count queries. The passing test is an
executor/history check, not a deployed multi-user Gateway test. The disposable
database container was removed.

On 2026-11-03, the `EllaiMemoryIndexTests|EllaiPostgresTests` integration
filter ran with a fresh disposable loopback PostgreSQL database, Qdrant and a
stopped loopback Redis endpoint: **24 passed, 0 failed, 14 skipped**. Command:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiMemoryIndexTests|FullyQualifiedName~EllaiPostgresTests' --logger 'console;verbosity=quiet'`.
The enabled fixture covered framework session recovery, authorized retrieval,
and Redis outage handling; live Muse and SQLAUTH-gated cases remained skipped.
The fixture containers were stopped. An earlier attempt started tests during
PostgreSQL initialization and failed; waiting for the final TCP listener avoided
that startup race. A second attempt used a prohibited outage port and was
rerun with an unused port in the permitted range. This does not establish
deployed Gateway acceptance or noncooperative tool termination.

An experimental verified-segment path now permits an English answer to reach
the transient feed before model completion when its current observations pass
the existing retained-history authorization checks. Each
segment is structurally validated and the session, retained history, and outbox
lease are checked before publication. Private tool observations are rechecked
through the existing history authorization logic; observations that cannot be
revalidated remain buffered. Owned-case counts retain their server-built final
answer. The provider must emit the optional segment envelope; deployed
Gateway behavior, multi-instance retry fencing, and independent
security review remain unverified. This is not complete streaming acceptance.
The focused synthetic framework test `VerifiedSegmentArrivesBeforeProviderCompletes`
passed 1/1. The existing framework, runner and response-store filter passed
118/118; the worker and isolation filter passed 53, skipped 1 (PostgreSQL
fixture unavailable). Gateway build passed with `-p:WasmBuildNative=false`;
the default build failed at native browser linking because `emcc` was not on
PATH. These changes and the wider framework migration remain uncommitted.

On the configured live Muse endpoint, `LiveMuseEmitsVerifiedSegmentBeforeCompletion`
passed 1/1 for a synthetic greeting: the segment callback occurred before the
provider stream ended and the final answer matched. This is an agent-level
probe, not proof of deployed Gateway delivery or tool-derived streaming. After
guarding against a repair after provisional speech, the framework, runner and
response-store filter passed 119/119.

The in-process Gateway worker test
`FrameworkWorkerPublishesVerifiedSegmentBeforeModelFinishes` passed 1/1: a
synthetic model held open after its first segment, which reached the response
feed while the outbox turn was still processing, then completed durably. It
does not exercise deployed Gateway processes or a live model through Gateway.

The current streaming implementation also admits the existing evidence-free
clock, capability and observation-validation tools when their results succeed.
The private-observation revalidation path has not yet been verified end to end
or with a live provider.

Isolated Ellai reads now set an absolute 35-second case-authority read deadline.
For PostgreSQL, each affected read transaction sets a server-side
`transaction_timeout` no longer than the remaining deadline or a pre-existing
shorter timeout. PostgreSQL terminates an overdue session and releases its
locks even if the client ignores cancellation. This applies only to read leases
inside isolated Ellai operations; write leases are unchanged. The configured
PostgreSQL 17 supports this setting; an older server without it fails the
affected read closed. This bounded transaction lifetime is an intentional
change to prevent abandoned reads retaining case-authority locks. Classification
evidence is now isolated through the same bounded pool. With disposable
PostgreSQL 17, the isolation filter passed 28/28, including a held read that
blocked a writer until the server deadline released the lock and a check that
the read deadline did not constrain the write lease. The fixture container was
stopped. This does not prove deadlines for non-PostgreSQL implementations or
that arbitrary non-database work terminates.

`CancelledIsolatedReadCannotRetainPostgresAuthorityLockPastDeadline` passed
1/1 with disposable PostgreSQL: the caller observed cancellation while the
isolated operation continued, and a blocked writer acquired the case lock
after the database deadline before the held operation was released. The test
uses a two-second injected deadline; the production isolated-read deadline is
35 seconds. This verifies the combined isolation and database-lock path, not
termination of arbitrary non-database work.

Read-only topic and evidence tools now use the isolation pool as well. Their
nested derived-index reads use a separate bounded pool to avoid pool starvation.
The existing authorized recall, held-index fallback, private-streaming and
grant-revocation PostgreSQL/Qdrant fixture passed 1/1 after this change; the
isolation filter passed 29/29 with disposable PostgreSQL. Custom non-cooperative
work can still occupy a pool slot until it returns, and State remains
cooperative because it can persist case-result references.

`PrivateToolSegmentRequiresFreshAuthorization` passed 2/2 with a controlled
framework tool response: an allowed revalidation released the segment before
model completion, and a denied revalidation did not. This tests the framework
fence with a synthetic authorization callback, not a PostgreSQL grant change.
`StreamObservationAuthorizationRechecksCaseAccess` also passed 1/1: the
runner's actual observation revalidation allowed a fixture case and denied it
after the queue fixture revoked access. This is not a PostgreSQL grant test.

Security review found that a failed current-observation check could previously
fall back to a buffered final answer. That check now stops the turn on denial,
and the final decision rechecks current observations even when the provider
omits segments. Four focused synthetic cases passed, including revoked evidence
before a buffered final answer. A disposable PostgreSQL/Qdrant grant-revocation
streaming scenario subsequently passed with a synthetic model. Provisional content validation and cross-instance
publication fencing remain open security findings.

An evidence-backed provisional segment is now rejected if it has no claims,
if any claim lacks evidence, or if a claim's text differs from the segment.
Focused tests passed 2/2 for rejection of an empty claim list and streaming a
matching claim with an observed evidence reference.
The segment must additionally match a complete line in each cited tool result;
paraphrases are buffered for the final answer. Focused tests cover both an
exact match and unsupported statements, including a misleading substring,
with an otherwise valid reference. The PostgreSQL/Qdrant private-streaming and
revocation fixture also passed after this stricter check.
This narrows the finding but does not prove that an observation semantically
supports its claim. The user will perform live testing; it is not a prerequisite
for preparing implementation and commits, and remains unverified here.

Provisional publication now takes PostgreSQL row locks on the session and the
claimed utterance, checks owner, epochs, attempt and unexpired lease, and invokes
the in-memory publication while both locks are held. The in-memory store uses
its existing gate. Stores without the new fence do not publish provisional
segments. A two-connection PostgreSQL fixture confirmed that an epoch change
waits for a publication in progress and that old epochs and reclaimed attempts
cannot publish; the focused test passed 1/1. The broader worker and isolated
tool filter passed 58/58. This reduces the publication race; failure of the
database connection during the in-memory callback and actual multi-process feed
delivery have not been demonstrated, so the security finding is not yet
accepted as closed.

Text-feed and audio delivery now also compare each provisional chunk with the
durable attempt, lease and session epoch. Focused tests passed for valid current
delivery, a reclaimed attempt, expiration, and an epoch change; a Gateway test
confirmed reclaimed text is omitted and its audio returns 404. PostgreSQL
tests passed 2/2 for publication locking and current-attempt evidence. These
checks narrow stale-delivery risk but do not prove network-partition behavior
across deployed Gateway processes.

`GuestCancellationOnAnotherHostHidesLocalResponseAndAudio` now uses a
provisional chunk and passed 1/1 with two in-process hosts and disposable
PostgreSQL. Cancellation on the second host stopped audio already streaming
from the first, hid its stale text, and rejected a later audio request. This
is not a process-level network-partition test; the user will perform live
testing after rollout.

The audio monitor now checks the durable attempt as well as the session while
provisional speech is in progress. Two focused two-host PostgreSQL tests passed
2/2: both remote cancellation and attempt reclamation stopped the held audio;
reclaimed text was hidden. The monitor checks once per second, so this is not
an instantaneous revocation guarantee.

The existing `FrameworkWorkerRecallsAuthorizedCaseFromPostgresAndQdrant`
fixture passed 1/1 using disposable PostgreSQL and Qdrant after the authority
fence change. It exercises permitted recall, derived-index outage, a held
index read, and grant expiry. Its first run failed because an empty, fixed
case-recall result was treated as unreplayable; the executor now recognizes
only the broker's bounded, unexpired empty-result forms as non-sensitive. Both
fixture containers were stopped. This is a synthetic model integration test,
not live Gateway acceptance.

The same fixture now additionally revokes the PostgreSQL grant after the tool
returned an authorized case and before the synthetic model's final decision.
It passed 1/1: the case appeared in the tool observation before revocation,
but the turn did not complete or persist an assistant answer after revocation.
The fixture also now streams a verbatim, evidence-backed case segment through
the actual Gateway worker and PostgreSQL publication fence before revoking the
grant. The segment appeared while authorized; the subsequent final answer was
not persisted. This passed 1/1 against disposable PostgreSQL and Qdrant with a
synthetic model; both containers were stopped.

The current application regression suite passed 1,172, failed 0 and skipped
17 with `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj
--no-restore -m:1 -nr:false --logger 'console;verbosity=quiet'`. The skipped
cases include live-provider probes, which the user will run; those do not hold
the implementation or commit. The user has authorized rolling all services from `infra/compose.yml`
after the remaining implementation/security findings are closed. The
database-side read-deadline capability was committed as `0c45cd2`; its
PostgreSQL lock-release test passed again after the commit. The Gateway wiring
and the remaining migration changes are not part of that commit.
After the scope-epoch delivery fence, the same application suite passed 1,176,
failed 0 and skipped 17; the focused response-store and authorization checks
passed 162/162, and the PostgreSQL two-host delivery filter passed 4/4.
Per the user's instruction, the security acceptance review uses the available
security-reviewer skill rather than a separate reviewer; the open findings
still require implementation and verification.

For the requested Compose rollout, the classification worker is configured
with Hugging Face and Transformers offline mode to prevent model downloads.
This Compose setting was committed separately as `85632e0` and Compose
configuration validation passed after the commit.
The existing classification volume contains the pinned BART revision and its
configuration and weights; the native model manifest's files all exist at
their recorded sizes, and the existing Kokoro directory is present. This is a
local file-presence/size check, not an inference or hash verification. Service
image builds may download runtime packages; they do not provision model weights.

After wiring the current-observation authorization check, the existing
framework, history-authorization and utterance-runner filter passed 163/163.
The Gateway worker and privileged-route filter passed 27, skipped 4; the
skipped cases require live-provider or PostgreSQL/multiple-host fixtures.
These checks do not exercise a private evidence segment during model-time
streaming and do not establish independent security or deployed acceptance.

Subsequently, the disposable PostgreSQL/Qdrant framework worker test passed
with an authorized private segment and denied final persistence after a grant
was revoked. Two-host PostgreSQL checks also passed for cancellation and
attempt-reclaim filtering, including stopping provisional audio. These checks
do not establish instantaneous revocation during a network partition.

The language and classification candidate images were built from existing
local images without downloading model weights. The language candidate imports
the locally sourced SciPy 1.16.3 and NumPy 2.3.4; the classification consumer
imports and its source hash matches the checkout. These are candidate-image
checks, not deployed-service acceptance. The existing TTS image matches the
current service sources and lock file. The optional MinIO bootstrap is for
initial provisioning and refuses to modify an existing bucket.
The Gateway candidate also completed its locked restore and Release publish
using the local SDK package feed; the browser runtime package was checked
against the official NuGet catalog SHA-512. The publish warned that browser
native references were not linked. No model weights were downloaded.

Security review remains open: a segment emitted before any evidence is observed
can contain unsupported model text before the final decision is validated.
The read-lease deadline limits PostgreSQL authority locks, but a cancellation-
ignoring operation can still occupy an isolation slot and the state path can
perform a session snapshot write. These limitations must not be reported as
fully closed by the passing focused checks.

### Evidence-free provisional output verification — 2026-11-03

An evidence-free model segment could previously reach the provisional feed
before the final decision was validated. The framework adapter now buffers such
segments and publishes only the validated final decision through the existing
final-text callback. Evidence-backed segments still require matching claims,
verbatim observation lines and current authorization before early publication.
Ordinary evidence-free answers therefore wait for the provider to finish;
framework streaming and early publication of eligible evidence-backed segments
remain available.

PASS: the new synthetic regression first failed on the former implementation,
then passed for both a matching segment and a segment that differed from the
final answer. PASS: the full Application suite reported 1,177 passed, 17
skipped and 0 failed using
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'`.
PASS: the Gateway worker and registration filter reported 47 passed, 0 failed
using
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiUtteranceWorkerOutcomeTests|FullyQualifiedName~EllaiReasoningRegistrationTests' --logger 'console;verbosity=minimal'`.
The worker fixture held a synthetic model open and checked that no
evidence-free answer reached the feed before completion.

NOT VERIFIED: the updated live Muse probe, deployed Gateway and voice delivery.
This change does not prove semantic safety of evidence-backed text, close the
cross-process publication finding, or make noncooperative tool operations stop.
In particular, the state tool may persist session references, so detaching an
arbitrary timed-out operation without a durable write fence is unsafe.

### Concurrent topic insert verification — 2026-11-03

A controlled concurrent PostgreSQL insert reproduced a lost case-result
snapshot: the caller that lost the insert race returned the competing row
without applying its own values. An initial correction retried that update,
but a further audit showed that an older delayed attempt could overwrite a
newer result. The current implementation reports a concurrency failure for
the competing insert and preserves the winner. It propagates unrelated
database update failures and verifies the session and title before modifying
a row found by its stable identifier.

PASS: `EllaiPostgresTopicTests` reported 4 passed, 0 failed against a
task-created loopback PostgreSQL 16 container with the topic table and its
production-named primary key. The deterministic race test failed against the
previous implementation and passed after the initial correction. The command was
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~EllaiPostgresTopicTests --logger 'console;verbosity=minimal'`,
with the existing disposable SQL fixture enabled. The container was removed.
NOT VERIFIED: the full production schema, deployed service, cancellation during
a database commit, and independent review. This correction does not resolve the
noncooperative tool timeout or establish serialized AgentSession restoration.

The same PostgreSQL fixture was subsequently expanded to check that an
unrelated unique constraint is propagated and that a colliding topic
identifier cannot alter another session's row. PASS: 6 passed, 0 failed with
the command above against a fresh PostgreSQL 16 container. Its data directory
was a task-created tmpfs, and the container was removed after the run.

The updated evidence-free buffering probe was subsequently run with synthetic
input against the configured live Muse route, together with official-client
text, framework brokered-tool and streaming-cancellation probes. PASS: 4
passed, 0 failed, 0 skipped using the Application test project with
`--no-build --no-restore -m:1 -nr:false` and filter
`LiveMuseBuffersEvidenceFreeSegmentsUntilDecisionCompletes|LiveOfficialChatClientReturnsSyntheticText|LiveFrameworkAgentInvokesBrokeredTool|LiveMuseStreamingCancellationStopsFrameworkTurn`.
The configuration came from the running local Gateway, without restarting it.
These tests ran in the test process against the provider, not through the
deployed Gateway or a physical voice device.

The entire current `EllaiLiveProbeTests` class was then run with the same
live configuration and synthetic inputs: PASS, 17 passed, 0 failed, 0 skipped
in 2 minutes 29 seconds. Command:
`dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter FullyQualifiedName~EllaiLiveProbeTests --logger 'console;verbosity=minimal'`.
This run includes synthetic upload and realtime ASR, framework-native tool
calls, provider streaming and cancellation. It does not demonstrate stability
across future provider calls, deployed Gateway execution, microphone capture,
or mobile-device behavior.

### Static state cancellation boundary — 2026-11-03

The Gateway now runs the state tool's static feature projection through the
existing bounded read-isolation pool. A missing projection or a sole
`projection=feature` argument has no case-result snapshot write; if its
scoped operation ignores cancellation, the caller can stop waiting while the
scope remains owned by the held operation. At that stage queue projections,
extra arguments, unknown projections and unclassified tools remained attached;
the later fenced PostgreSQL queue path is described below.

PASS: the focused selection/cancellation filter reported 20 passed, 0 failed.
PASS: `EllaiIsolatedToolCallsTests|EllaiUtteranceWorkerOutcomeTests` reported
64 passed, 2 skipped, 0 failed with
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiIsolatedToolCallsTests|FullyQualifiedName~EllaiUtteranceWorkerOutcomeTests' --logger 'console;verbosity=minimal'`.
The two skips require the disposable PostgreSQL authority fixture. The new
cancellation tests exercise the worker's selection boundary with held synthetic
operations; they are not deployed Gateway tests. Queue snapshot writes and
arbitrary noncooperative work still do not have a complete timeout guarantee.

### Topic update concurrency and EF metadata — 2026-11-03

A second PostgreSQL regression reproduced an older loaded topic overwriting a
newer save. The topic mapping now uses PostgreSQL's `xmin` as its EF
row-version token. Stale updates and competing inserts fail visibly; the state
tool reports that result references could not be retained instead of claiming
they were saved. PASS: the PostgreSQL topic filter reported 8 passed, 0 failed
against a task-created PostgreSQL 16 database with the topic table; the
delayed-update regression failed before the row-version mapping.

The EF model requires a metadata-only migration
`20261103210000_EllaiTopicConcurrency`: PostgreSQL already supplies the
system column. PASS: the Infrastructure build, EF pending-model check, and
generated upgrade and downgrade scripts. The scripts only insert or delete
the migration-history entry. The installed EF CLI was 10.0.12 and warned that
the runtime was 10.0.14.

PASS: `DatabaseMigrationDeploymentTests` ran against a task-created
PostgreSQL 17/PostGIS fixture, upgraded the full schema, rolled the metadata
migration back, verified that `xmin` remained readable, and reapplied it.
Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~DatabaseMigrationDeploymentTests --logger 'console;verbosity=minimal'`.
The temporary database was deleted by the fixture and the tmpfs-backed
container was removed. NOT VERIFIED: deployed Gateway startup.

PASS: a combined worker/isolation and representative SQLite-backed integration
filter reported 83 passed, 2 skipped, 0 failed:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~DemoDataTests|FullyQualifiedName~EmailChangeServiceTests|FullyQualifiedName~GeographicRegistryTests|FullyQualifiedName~EllaiIsolatedToolCallsTests|FullyQualifiedName~EllaiUtteranceWorkerOutcomeTests' --logger 'console;verbosity=minimal'`.
The skipped tests require the PostgreSQL authority fixture. These checks do not
exercise SQLite topic persistence.

The row-version token detects changes between reading and updating the topic;
it does not by itself fence a cancelled attempt whose old case read finishes
after a newer attempt has already saved. The subsequent PostgreSQL
outbox-attempt fence is described below.

### Case-result snapshot attempt fence — 2026-11-03

The default PostgreSQL Gateway path now persists case-result snapshots while
holding the session and claimed-utterance row locks in the same transaction
as the topic write. It checks owner, scope and interaction epochs, attempt
number, processing state and unexpired lease, and rechecks the lease after
the write before committing. A reclaimed, cancelled or expired attempt cannot
start a new snapshot write through this fence. The worker uses the fence only
when its broker's topic store is the PostgreSQL store sharing the same scoped
database context. Other store arrangements retain their existing attached,
cooperative behavior.

PASS: five focused tests against disposable PostgreSQL 17/PostGIS covered
the framework worker's successful snapshot and lock behavior, foreign
owner/scope, expired and reclaimed attempts, session cancellation, and
rollback when a lease expires before commit. Command:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~FrameworkWorkerPersistsFencedCaseResultSnapshot|FullyQualifiedName~FrameworkWorkerHoldsSessionFenceDuringCaseSnapshotWrite|FullyQualifiedName~CaseResultSnapshotRejectsReclaimedOrCancelledAttempts|FullyQualifiedName~CaseResultSnapshotHoldsSessionFenceUntilCommit|FullyQualifiedName~CaseResultSnapshotRollsBackWhenLeaseExpiresBeforeCommit' --logger 'console;verbosity=minimal'`.
The tmpfs-backed fixture container was removed. PASS: Application suite,
1,177 passed, 17 skipped; worker/isolation/registration filter, 73 passed,
2 skipped. The latter skips require the PostgreSQL authority fixture.

This fence protects the tested PostgreSQL attempt transitions but does not
prove transaction behavior during a network partition or guarantee termination
of a cancellation-ignoring topic write. At this stage queue state operations
remained attached; the later bounded-worker change is described below.

The snapshot transaction now also applies an absolute server-side
`transaction_timeout`, defaulting to 35 seconds and respecting a shorter
existing setting. PostgreSQL without this capability fails the snapshot write
closed; the configured Compose database is PostgreSQL 17. PASS: six focused
PostgreSQL 17/PostGIS tests, including a held client operation where a
two-second injected server deadline released the session lock before the
client was released and left no snapshot. Command: the five-test filter above
plus `FullyQualifiedName~SnapshotTransactionDeadlineReleasesSessionLockWhenClientStalls`.
The tmpfs-backed fixture was removed. This bounds tested database lock
retention; it does not terminate arbitrary application work or prove behavior
under a network partition.

### Fenced queue timeout — 2026-11-03

The framework executor now passes the current tool's server-owned absolute
deadline to the PostgreSQL snapshot fence. The transaction deadline is the
earlier of that limit and the existing 35-second cap. The Gateway uses a
separate bounded isolation pool for queue state work only when its scoped
queue and topic stores match the expected services and the topic store shares
the fence's PostgreSQL context. A fresh detached scope must pass those checks
again; otherwise the tool fails closed. Other store arrangements remain
attached. Separate pools prevent queue calls from consuming all of the slots
needed by their nested authorized reads.

PASS: 12 focused cases against disposable PostgreSQL 17/PostGIS and the
in-process Gateway worker. They include a two-second tool deadline with a
snapshot operation that ignores cancellation: the timed-out turn failed, a
subsequent turn completed while the old operation remained held, and releasing
the old operation did not persist a snapshot. The filter also covered
reclaimed, cancelled, foreign, expired and lock-contending attempts and
unrecognized request shapes. Command:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~FrameworkWorkerPersistsFencedCaseResultSnapshot|FullyQualifiedName~FrameworkWorkerHoldsSessionFenceDuringCaseSnapshotWrite|FullyQualifiedName~FrameworkWorkerTimesOutStalledSnapshotAndProcessesNextTurn|FullyQualifiedName~CaseResultSnapshotRejectsReclaimedOrCancelledAttempts|FullyQualifiedName~CaseResultSnapshotHoldsSessionFenceUntilCommit|FullyQualifiedName~CaseResultSnapshotRollsBackWhenLeaseExpiresBeforeCommit|FullyQualifiedName~SnapshotTransactionDeadlineReleasesSessionLockWhenClientStalls|FullyQualifiedName~FencedQueueSelectionRejectsUnrecognizedShapes' --logger 'console;verbosity=minimal'`.

These tests establish the synthetic in-process PostgreSQL path, not deployed
Gateway acceptance or network-partition behavior. A cancellation-ignoring
operation can still retain a bounded isolation slot until it returns; custom
stores without the transactional fence retain cooperative timeout behavior.

Further verification: PASS, the PostgreSQL session class reported 22 passed,
16 skipped and 0 failed on its final run against the disposable fixture. An
earlier run reported one failure in
`FrameworkWorkerRestoresHistoryAfterManyRepeatedToolObservations`; the quiet
logger did not capture the cause. That test passed in isolation and in the
full-class rerun. Its 300-tool-call first turn now has a 30-second fixture
allowance rather than 10 seconds; the cause of the original failure is not
established.

PASS: the Application suite reported 1,177 passed, 17 skipped, 0 failed.
The first full default Integration run reported 787 passed, 327 skipped and
one failed audio-worker case; quiet output did not capture its cause. All
seven cases in that audio theory passed on immediate rerun. A subsequent full
Integration run reported 788 passed, 327 skipped and 0 failed. Command:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --logger 'console;verbosity=normal'`.
The intermittent initial failure remains unexplained; a passing rerun does not
prove it cannot recur.

### Verification checkout delivery — 2026-11-03

The verified changes were committed on the designated verification checkout's
active branch without pushing:

- `fa15517`: buffer evidence-free provisional responses. Post-commit focused
  Application checks passed 7/7.
- `95a173a`: protect topic snapshots against concurrent overwrites, including
  the metadata-only row-version migration. Post-commit PostgreSQL topic and
  full-schema migration checks passed 9/9 on a disposable PostgreSQL 17/PostGIS
  instance.
- `894bf78`: fence and bound PostgreSQL queue snapshot execution. Post-commit
  fence, timeout and request-selection checks passed 12/12 on a separate
  disposable PostgreSQL 17/PostGIS instance.

Both post-commit database fixtures used tmpfs and were removed. These commits
are on the verification branch; the separate `interactive-ellai` checkout
was not changed. The earlier intermittent integration failure, network
partition behavior, custom-store cooperative timeout, and deployed
Gateway/device acceptance remain open as described above.

### Provisional delivery read race — 2026-11-03

A deterministic feed regression reproduced disclosure of a provisional
segment when its outbox attempt was reclaimed between the conversation and
session reads. The feed and provisional-audio path now ask a delivery fence
to check owner, scope, interaction epoch and attempt together before delivery.
The PostgreSQL implementation uses a single query for that check; the
in-memory implementation checks under its store lock. If a custom outbox
does not implement the fence, provisional content is withheld. A changed
owner also causes the feed to omit the previously read conversation.

PASS: the race regression failed before the correction. After commit
`964ce63`, nine focused tests passed against in-memory and disposable
PostgreSQL 17/PostGIS fixtures. They cover reclaim during feed and audio reads,
owner change, a failed durable check, the PostgreSQL interleaving, and two-host
audio cancellation and reclaim. Command:
`dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~PostgresFeedRejectsAttemptReclaimedDuringRead|FullyQualifiedName~ProvisionalPublicationHoldsSessionAndAttemptFence|FullyQualifiedName~GuestCancellationOnAnotherHostHidesLocalResponseAndAudio|FullyQualifiedName~ReclaimedAttemptOnAnotherHostStopsProvisionalAudio|FullyQualifiedName~FeedHidesProvisionalChunk|FullyQualifiedName~FeedOmitsConversationWhenOwnerChangesDuringRead|FullyQualifiedName~AudioHidesProvisionalChunkWhenAttemptIsReclaimedDuringRead|FullyQualifiedName~ProvisionalDeliveryFailsClosedWhenDurableCheckIsUnavailable' --logger 'console;verbosity=minimal'`.
The PostgreSQL fixture was tmpfs-backed and removed. The pre-commit worker and
route filter passed 68, skipped 5. The unavailable-store case is a synthetic
fault, not a process-level network-partition test; an authorization change
after a successful check can still race bytes already in flight. Deployed
Gateway, independent security review, and physical voice-device behavior
remain NOT VERIFIED.

### Gateway recovery on the verified branch — 2026-11-03

PASS: on commit `4a09962`, the following focused integration run passed 3/3
in 57 seconds, with no skips:

```powershell
dotnet test tests\SwiftAid.Integration.Tests\SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~GuestHttpFrameworkUsesLiveMuseAndDurableMultiTurnHistory|FullyQualifiedName~GuestHttpLiveMuseRecoversConversationAfterTestHostRestart|FullyQualifiedName~GuestHttpConversationRecoversAfterTestHostRestartWithPostgres' --logger 'console;verbosity=minimal'
```

The run enabled `ELLAI_LIVE` and the framework PostgreSQL fixture, supplied
the existing Gateway's Ellai provider configuration privately to the test
process, and used synthetic conversation data. Two tests exercised live Muse;
the third used a deterministic model. They crossed an in-process Gateway and
real PostgreSQL boundary, including multi-turn context, a brokered tool,
streamed responses, and conversation recovery across test-host restart. The
PostGIS 17 fixture was a task-owned container using tmpfs; it was removed after
the run. No running application service or shared database was restarted.

NOT VERIFIED: the deployed Gateway process, browser or device behavior,
production data-protection configuration, and actual network partition or
instantaneous revocation behavior. This run does not establish those gates.

### Authorized framework case recall — current branch verification

PASS: on commit `ce343af`, the following focused integration run passed 2/2
in 1 minute 2 seconds, with no skips:

```powershell
dotnet test tests\SwiftAid.Integration.Tests\SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~FrameworkWorkerRecallsAuthorizedCaseFromPostgresAndQdrant|FullyQualifiedName~LiveMuseWorkerRecallsAuthorizedCaseFromPostgresAndQdrant' --logger 'console;verbosity=minimal'
```

The fixture enabled live Muse and dedicated framework PostgreSQL and Qdrant
fixtures. One test used a synthetic model and one used Muse. Both used synthetic
case data, a real framework worker, real PostgreSQL authorization, and a real
derived Qdrant index; their scenarios cover authorized recall, index failure,
and grant expiry. The synthetic-model fixture additionally exercises a held
index read and revocation before final publication. Provider settings were
supplied privately to the test process.

NOT VERIFIED: a deployed Gateway, browser or device journey, and a real
network partition. These are worker-boundary tests and do not demonstrate
instantaneous revocation of bytes already delivered.

### Authorized analytics — current branch verification

PASS: on commit `ce343af`, the following focused integration run passed 2/2
in 15 seconds, with no skips:

```powershell
dotnet test tests\SwiftAid.Integration.Tests\SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~LiveMuseExecutorUsesAuthorizedCountForOrdinaryQuestion|FullyQualifiedName~LiveMuseUsesAuthorizedPostgresCountAndCitesObservedEvidence' --logger 'console;verbosity=minimal'
```

Both tests used live Muse, a synthetic case fixture and disposable PostgreSQL.
They exercised the authorized managed-case count through framework function
calling and checked the resulting evidence. The executor test also checked a
follow-up with the same date window and omission of prior context after grant
expiry. This proves the tested owned-case count; arbitrary aggregations and
deployed Gateway behavior remain NOT VERIFIED.

### Qdrant registration enablement — 2026-11-03

The Gateway previously registered the Ellai Qdrant index and projection worker
whenever an endpoint was present, even with `Qdrant:Enabled=false`. Because the
provider validator skips endpoint and credential checks while disabled, this
could also initiate connections using configuration that had not passed those
checks. Gateway registration now follows the enable flag, after infrastructure
option validation. The local Compose file explicitly sets the flag to preserve
its intended Qdrant integration. Other deployments that want this integration
must set `Qdrant:Enabled=true` and provide valid endpoint and credentials.

PASS: `dotnet test tests\SwiftAid.Integration.Tests\SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiMemoryRegistrationTests|FullyQualifiedName~ProviderConfigurationTests' --logger 'console;verbosity=minimal'` passed 25/25. The focused cases check enabled and disabled registration and reject invalid enabled configuration; they do not launch the deployed Gateway. Deployment and production transport remain NOT VERIFIED.

PASS: the default integration regression suite on commit `7c4d013` passed 795,
failed 0, and skipped 328 in 33 seconds using `dotnet test
tests\SwiftAid.Integration.Tests\SwiftAid.Integration.Tests.csproj --no-build
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --logger
'console;verbosity=minimal'`. Skipped opt-in database, live-provider, and browser
fixtures are not counted as verified by this run.

### Current rendered guest conversation — 2026-11-03

PASS: the current source at `f922c62` was published with `dotnet publish
src\SwiftAid.Gateway\SwiftAid.Gateway.csproj --no-restore -m:1 -nr:false
-p:WasmBuildNative=false -o <task-candidate> --verbosity quiet`. A separate
loopback Gateway used live Muse, synthetic prompts, a newly migrated disposable
PostGIS 17 database and a task-owned NATS server. The command `node
tests\SwiftAid.Ui.Tests\ellai-conversation.browser.cjs` passed twice in
headless Chromium, with `SWIFTAID_RUN_LIVE_ELLAI=1` and the candidate URL. The
second run additionally set a synthetic word prompt and a follow-up, and
verified that the word was recalled. Both runs checked the rendered answer,
refresh restoration, the 390-pixel thinking indicator and reduced motion; no
page errors were observed. The fixture used a temporary module shim to the
locally installed Playwright Core package.

The first candidate startup lacked its case-processing configuration; a later
startup lacked a valid isolated NATS configuration; and a temporary key-ring
directory failed filesystem security validation. These failed starts preceded
the working isolated setup. The running candidate logged an unavailable NATS
notification consumer; this browser check does not validate notifications. An
end-session request was observed as HTTP 499 during browser teardown, so this
run does not establish completed cancellation. A response-feed timing sample
arrived after the script recorded completion, showing that its asynchronous
measurement is not reliable evidence of model-time streaming. Physical voice,
production data protection and deployed behavior remain NOT VERIFIED.

The candidate process and both task-owned containers were stopped. The
candidate publish and task-owned temporary key and module directories were
retained; automated cleanup of the latter directories was rejected by the
execution policy.

### Rendered end-session acknowledgement — 2026-11-03

The browser conversation test now waits for the end-session POST response
and reads the durable session before closing the browser. This prevents the
test's own teardown from interrupting the request and avoids reporting PASS
before cancellation is confirmed.

PASS: `node tests\SwiftAid.Ui.Tests\ellai-conversation.browser.cjs` passed
twice against the previously published `f922c62` loopback Gateway, live Muse,
a newly migrated disposable PostgreSQL database and task-owned NATS. One run
included a second turn that recalled a synthetic word; both observed HTTP 200
for End and read the session back with status `Cancelled`. The second run
also exercised the final test output after the assertion. The local NATS
notification consumer reported unavailable; notifications, cancellation while
the model is still running, actual devices and deployed behavior were not
verified by these browser runs. The test-owned Gateway and containers stopped;
the previously retained candidate and key/module directories remain.

### Rendered cancellation during processing — 2026-11-03

PASS: `node tests\SwiftAid.Ui.Tests\ellai-cancel-rendered.browser.cjs`, with
`SWIFTAID_RUN_LIVE_ELLAI=1` and the isolated loopback Gateway URL, exercised
the rendered guest End control after a synthetic turn reached `Processing`.
The test observed HTTP 200, a durable `Cancelled` session and turn, no stored
assistant answer and no visible answer chunks. It used headless Chromium, the
previously published `f922c62` Gateway, live Muse configuration, a newly
migrated disposable PostGIS database and task-owned NATS. `Processing` alone
does not prove that the provider had received the request before cancellation.

The first test attempt failed while the harness read the session-start
response body; it was corrected to use the client's persisted session ID and
the rerun passed. The fixture's NATS notification consumer was unavailable.
The Gateway and disposable containers were stopped. The previously retained
candidate and key/module directories remain; physical-device and deployed
behavior are NOT VERIFIED by this test.

### Final evidence recheck without provisional streaming — 2026-11-03

The utterance runner installed the current-tool-observation authorization
callback only when a provisional stream callback was present. The framework
agent also uses that callback before returning a buffered final answer. Thus a
turn without provisional publication could omit that last observation check,
even though the ordinary session and historical-context fences still ran. The
runner now installs the observation check regardless of streaming; provisional
publication remains conditional on its existing policy and language checks.

The expanded `StreamObservationAuthorizationRechecksCaseAccess` regression
first failed for `provisionalStreaming=false` because the callback was null;
the enabled case passed. After the fix, `dotnet test
tests\SwiftAid.Application.Tests\SwiftAid.Application.Tests.csproj --no-restore
-m:1 -nr:false --filter 'FullyQualifiedName~EllaiAuthorizedConversationContextTests|FullyQualifiedName~EllaiUtteranceRunnerTests|FullyQualifiedName~EllaiAgentFrameworkContractTests' --logger 'console;verbosity=minimal'`
passed 174/174. The two runner cases check an authorized case and then an
expired authorization using a synthetic queue. The existing framework test
also rejects a buffered final answer when its recheck denies access.

PASS: `dotnet test tests\SwiftAid.Integration.Tests\SwiftAid.Integration.Tests.csproj
--no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter
'FullyQualifiedName~FrameworkWorkerRecallsAuthorizedCaseFromPostgresAndQdrant'
--logger 'console;verbosity=minimal'` passed 1/1 using a synthetic model and
disposable PostgreSQL and Qdrant, including its existing revocation-before-final
scenario. That worker fixture uses its normal provisional callback; a separate
no-provisional PostgreSQL revocation run and independent security review remain
NOT VERIFIED. Both disposable containers were removed.
