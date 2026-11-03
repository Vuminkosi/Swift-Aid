# Ellai multi-agent preflight

Status: **IN PROGRESS — NO-GO for multi-agent implementation**. This report records
evidence, not acceptance of the complete migration or the multi-agent feature.

- Branch: `ellai-multi-agent-implementation`.
- Starting revision: `20e6d09f0c3436cf288123b15cdfee05dd41ce57`.
- Scoped vector-recall fence committed as `72fd5b0`; its post-commit focused
  HTTP-handler test run passed 5/5. An independent human security review has not
  been performed.
- Framework-session checkpoint committed as `29a3cba`; its post-commit focused
  authorization, codec and outbox test run passed 67/67. Independent human
  security and migration review remain outstanding.
- The user confirmed that the single-agent migration was completed. The existing
  migration inventory's opening status predates commits `e0ee9b8` and `20e6d09`.
  A separate accepted-baseline report has not been located in this worktree.
- Current worktree adds framework-session serialization in a completed-turn
  checkpoint. The checkpoint is bound to owner, application session and scope;
  model history is rebuilt from the current authorization-filtered projection.
  PostgreSQL completion locks the session and utterance and checks the live lease
  using the database clock before committing the answer and checkpoint together.
  Execution history does not load checkpoints in bulk: after current history
  authorization, a separate owner-scoped read loads at most one retained turn's
  checkpoint. Validation narration uses a short time-window event key so repeated
  requests for the same prompt do not repeatedly enqueue speech within that window.
  This work requires independent integrity/security review before deployment.

## Evidence gathered

| Check | Result and limits |
|---|---|
| Application suite, `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'` | PASS: 1,184 passed, 17 live-provider tests skipped. Ran before the additional live checkpoint probe was added. |
| Authorization, framework-session, outbox and guidance focused tests | PASS: 137 tests in the latest focused application run. Includes denied-history checkpoint exclusion, owner/scope isolation and stale or expired in-memory claim rejection. |
| Gateway worker, registration and guidance HTTP filters | PASS: 53 integration tests using test fixtures. These are not deployed-provider acceptance. |
| Disposable PostgreSQL/PostGIS 17 checkpoint, lock-expiry, scope-change and worker-restart tests | PASS: 2 tests on a task-created loopback container; model output was synthetic. Container removed. |
| Redis cache outage, closed disposable loopback port | PASS: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~UnavailableRedisReturnsExplicitFailureForReadWriteAndRemoval --logger 'console;verbosity=minimal'`, 1/1. `SWIFTAID_TEST_REDIS_DOWN_CONNECTION` pointed to a previously reserved and closed loopback port with 500 ms timeouts. Read, write and removal reported explicit failures; worker recovery and deployment outages are not established by this test. |
| Guidance HTTP admission tests | PASS: 6 tests against a local test host. Unregistered validation prompt codes and foreign sessions were denied. |
| Disposable Qdrant 1.19 vector and exact-reference tests | PASS: 2 tests, including vector session/scope isolation and exact recall after access is revoked, using synthetic fixtures. The task-created container and collections were removed. |
| Configured live Muse route, synthetic input | PASS: brokered-tool and streaming-cancellation probes, 2/2; a separate two-turn framework-session serialization/restoration probe, 1/1. These ran in the test process, not through the deployed Gateway. |
| Authorized aggregate count, disposable PostgreSQL + live Muse | PASS: `EllaiOwnedCaseCountPostgresTests`, 5/5, including two live-model cases and grant-expiry / changed-membership checks over synthetic records in PostgreSQL. The task-created database container was removed. This is not arbitrary analytics or a deployed Gateway journey. |
| Synthetic recorded voice, live ASR + Muse + local Kokoro | PASS: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~SyntheticSpeechFlowsThroughLiveTranscriptionMuseAndSpeech --logger 'console;verbosity=minimal'`, 1/1, 50 seconds total test duration. A task-created loopback relay reached the existing speech service and was removed. The fixture used an in-memory session and synthetic audio; this is not a deployed Gateway or device journey. |
| Guided report and guidance UI logic filters | PASS: 36 tests. A focused Chromium form fixture also passed at 1280/light and 390/dark, including focus and sequential highlighting; screen-reader behavior remains NOT VERIFIED. |

## Open preflight items

- NOT VERIFIED: accepted migration commit and acceptance report with deployment
  version, rollback and documented exceptions for all required gates.
- NOT VERIFIED: deployed Gateway session recovery and voice/TTS, physical mobile,
  authorized real-data search/analytics and the complete provider regression gate.
- NOT VERIFIED: current live Redis and Qdrant collection/key inventory and outage
  behavior in every deployment. On 2026-11-03, a read-only authenticated scan
  of the local Redis container found zero keys matching `swiftaid:*`; the initial
  unauthenticated scan returned `NOAUTH`. With no matching keys, their TTLs could
  not be checked. The local Qdrant collections endpoint listed
  `ellai-memory-exact` and `ellai-probe`; both reported green and no payload-index
  fields in collection metadata. No point payloads were read. Source inspection
  shows a 3,600-second default Redis maximum TTL, and exact-reference Qdrant reads
  filter by session, scope, identity, encoding and expiry, then recheck payloads.
  This limited local inventory does not establish outage behavior, vector search
  authorization, production indexes or other Redis key spaces.
- A source review found that vector search supplied session/scope filters but
  did not recheck returned payloads. The current worktree rejects payloads that
  do not match both requested values, rejects missing scope before making an
  HTTP request, and caps returned results. PASS: five focused HTTP-handler
  tests, including foreign, missing-scope and over-returned payloads. The
  Gateway source registers and projects the exact-reference index. The
  disposable Qdrant tests exercise vector filtering and exact-reference access
  rechecks; deployed behavior and independent security review remain NOT VERIFIED.
- NOT VERIFIED: independent review of checkpoint confidentiality, authorization,
  migration compatibility and concurrency. Do not enable or deploy this change
  until the migration is applied and these checks are complete.
- The multi-agent registry, scheduled agents, timeline activity/deduplication,
  officer escalation, and their acceptance matrix are not implemented by this slice.

The validation-walkthrough change displays and highlights one invalid field at a
time. Its new versioned HTTP route accepts a fixed prompt code, not entered form
values, and uses the existing owned-guidance admission and speech publication
path. A focused Chromium form fixture verified sequential focus and highlighting at desktop and mobile widths. A separate local Kokoro integration test produced nonzero speech audio and verified interruption eligibility. Actual guided-tour browser playback, physical-device behavior, and deployment remain NOT VERIFIED.
