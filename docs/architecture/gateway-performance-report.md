# Gateway ingestion verification and performance report

## Classification backfill continuation, 2026-11-01

PASS: real isolated Kestrel/Python/pinned BART CPU/PostgreSQL/JetStream integration with four synthetic reports. Reconciliation: **4 eligible, 1 newly classified, 2 reused, 1 unsupported-language abstention, 0 failed, 0 pending**. One English report had completed through the live broker path; another reused that fingerprint; a distinct English report required backfill inference. The integration test took approximately eight seconds including model startup. A measured live inference was 1,250.06 ms, with 1,418.80 ms for claim/inference/commit/redelivery/quarantine (startup excluded). These are single-run observations, not p95/p99 or sustained throughput. The four-record backfill checkpoint interval was 1.291 seconds. The corrected full suite passed ten tests.

PASS: Python 57 passed / three opt-in skips; 301 application/Ellai tests / three provider skips; Ruff, strict mypy and EF model/migration alignment. New DB-context restart, paused admission, immutable snapshot, duplicate start, configuration change and scope denial are covered. Whole-process inference crash, sustained GPU/CPU utilization, concurrent interactive load, historical 15k classification, 100k/million processing, downstream detections and deployment remain **NOT VERIFIED**. The earlier disposable 15k ingestion database was removed; supplied-file row counts are not classification output counts.

2026-11-01. Synthetic, disposable local Docker fixtures on Windows; .NET 10.0.14, PostgreSQL/PostGIS 17, NATS 2.15.1. No patient data.

## Final consolidated checks and delivery boundary

Subsequent classification work is measured in [the classification implementation report](gateway-classification-implementation.md#executed-validation-local-windows--synthetic-fixtures). Its single-report real BART/replay test does not change the 15,000-row ingestion measurement into a classification benchmark.

PASS: 91 integration tests, 2 opt-in skips, at `62608eb` with the disposable PostgreSQL, restricted import runtime and NATS fixture variables configured:

```powershell
dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~CaseImport|FullyQualifiedName~SharedWorkspace|FullyQualifiedName~EllaiImport|FullyQualifiedName~CaseRegistration|FullyQualifiedName~ManagedCaseRegistration|FullyQualifiedName~IngestionOutbox|FullyQualifiedName~JetStreamAcknowledgement|FullyQualifiedName~OrganizationMembership|FullyQualifiedName~OrganizationInvitation|FullyQualifiedName~DemoDataTests|FullyQualifiedName~CaseAuthorityLockHelperSource' --logger 'console;verbosity=minimal'
```

The two skips are browser and supplied-file load opt-ins, executed separately with the results below. Process-loss tests also have their own explicit opt-in and separate result. This combined check does not establish a running downstream intelligence pipeline.

PASS: `dotnet ef migrations has-pending-model-changes --project src/SwiftAid.Infrastructure --startup-project src/SwiftAid.Infrastructure --no-build` reported no pending model changes. EF tooling 10.0.12 reported its version difference from runtime 10.0.14; no schema check failure occurred.

PASS: `$env:GATEWAY_IMAGE='swiftaid/gateway:ingestion-20261101'` followed by `docker compose -f infra/compose.yml build gateway`. Image built at 06:50 UTC, ID `sha256:365d8913514854ea7b73674b47f04a15a6b92c728ac7bc9251b1b26e889e4e78`. Build source was `118e9ee`; the following `62608eb` change only affected tests/documentation. Build emitted trimming and browser SQLite native-reference warnings with native WASM/AOT disabled. Browser offline SQLite behavior is not verified by this build.

NOT VERIFIED: running this published image, shared Gateway recreation/migration, Android/iOS rebuilds, physical devices and live model providers. The isolated browser fixture below exercises actual component/service/database paths with synthetic authentication and policy; it does not substitute for deployment acceptance. The image and local ignored browser/measurement evidence are retained as delivery artifacts.

Overall result: **PARTIAL**. Maximum measured durable import is 15,000 reports. Completed downstream processing throughput, detection accuracy, million-record capacity and end-to-end surveillance readiness remain unproven. See the [delivery matrix](gateway-implementation-plan.md#delivery-status-at-the-end-of-the-bounded-implementation-slices).

## Verified dispatch slice

| Check | Result | Observed scope |
|---|---|---|
| Pre-fix outbox model regression | FAIL as expected | Both existing outboxes lacked lease identity/exhaustion state. |
| First real PostgreSQL/NATS run | 7 PASS, 1 FAIL | PubAck crash/replay uncovered duplicate acknowledgement rejection by `EnsureSuccess()`. Failure was corrected, not waived. |
| Corrected focused run | PASS, 25 tests, zero skips, about 2 s test duration | Model, concurrent claims, lease expiry/fencing, bounded retry, expired final attempt, ordered case revisions, exhausted predecessor/requeue, provider failure, real PubAck crash/replay, duplicate/error acknowledgement and existing job publisher contracts. |
| Infrastructure build | PASS | `dotnet build src/SwiftAid.Infrastructure/SwiftAid.Infrastructure.csproj --no-restore -m:1 -nr:false --verbosity quiet`; no warnings/errors. |
| Stream bootstrap with real repository ACL | PASS | Separate disposable broker; bootstrap identity created and verified bounded file stream. No shared broker mutation. |
| Final dispatch regression and Compose validation | PASS | 28 tests, zero skips, about 7 s; includes existing atomic intake attachment-binding tests. `docker compose -f infra/compose.yml --profile ingestion config --quiet` succeeded. |

Test command and environment variables are in [the runbook](gateway-operational-runbook.md). The crash scenario deliberately omits the database completion after a real broker PubAck, advances the fixture lease clock, republishes the same ID and verifies the same broker sequence plus successful durable completion. This is boundary fault injection, not an operating-system crash test.

## Capacity and remaining verification

Initial dispatch-slice limitations: full ingestion/processing load, sustained throughput, p95/p99 end-to-end latency, CPU/working-set limits, multi-node HA, actual process/network restart, full consumer result-before-ACK, statistical detection accuracy, physical devices and external providers were NOT VERIFIED at that point. Later supplied-file and restart results below supersede only their explicitly tested boundaries. Stream capacity settings are limits, not measured throughput.

The largest concurrency assertion in the initial functional run is twelve independent intake notices claimed by two database connections without overlap. Test timing is not a throughput benchmark. Additional measured slices will be appended without replacing these limits with estimates.

## CSV adapter and Ellai assistance slice

PASS: 330 focused application tests, 3 existing skips, zero failures (`--filter 'FullyQualifiedName~Ellai|FullyQualifiedName~CaseCsvImport|FullyQualifiedName~IntakePromotion'`). Includes existing promotion behavior after sharing the source-attributed report mapper.

PASS: actual supplied Zimbabwe 10,500 + Eswatini 4,500 CSV rows streamed, parsed and mapped to typed reports in one fixture test (508 ms reported test duration). Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~CaseCsvSuppliedFixtureTests`, with `SWIFTAID_TEST_CASE_CSV_DIRECTORY` set to the local `designs/data` directory. This is **15,000-row parsing/mapping evidence only**, not database ingestion throughput or full pipeline capacity. Provider-driven Ellai conversations and client UI use of the new tools remain NOT VERIFIED.

## Durable import and workspace browser slice

PASS: eight PostgreSQL/HTTP tests cover protected original rows, canonical case/receipt/outbox writes, commit-before-checkpoint recovery, current owner/workspace/epoch authorization, changed-row rejection, active leases, sealed completed imports, durable rejection details and HTTP authentication/antiforgery. Seventeen focused application tests cover CSV compatibility plus bounded UTF-8 batches, BOM, ordering, cancellation and end-of-file resume.

PASS: cached Chromium 1248, actual Gateway `CaseImports` component and `WorkspaceImportClient`, real PostgreSQL and registration service in an isolated Kestrel fixture. Upload 250 synthetic rows, pause after a batch, refresh, resume the selected original file, finish, refresh again and restore all 250 saved cases. Verify exactly 250 receipts and 500 events. An additional two-row attempt retains one invalid row and replays the existing valid row; rejection details restore after refresh. No JavaScript page errors. Screenshots inspected at 1440×1000 and 390×844; no horizontal page overflow at 390 px. Initial full happy-path interaction measured 12.5 s; final expanded browser fixture test completed in about 14 s. These are functional timings, not a sustained-throughput claim.

The first negative browser extension failed because it selected a file before the server had replaced the completed import's disabled picker. The harness now waits for the actual New import render; no product validation was bypassed. An earlier scope-query refactor failed SQL translation; member-initializer projection fixed it and all eight database/HTTP checks passed afterward.

Command: integration test filter `FullyQualifiedName~CaseImportBrowserTests`, with `SWIFTAID_RUN_IMPORT_BROWSER=1`, the PostgreSQL fixture variables, installed Playwright/Chromium (`NODE_PATH` / optional `SWIFTAID_CHROMIUM_PATH`). The checked-in browser script is invoked by the test, not pointed at the shared Gateway. Screenshots and result JSON are local ignored evidence under `tests/SwiftAid.Ui.Tests/artifacts/pipeline-imports-20261101`.

Limitations: identity and policy are deterministic fixtures; live account provisioning, native Android/iOS imports, dark-theme/screen-reader coverage, external providers and deployment to the shared Gateway are NOT VERIFIED. The browser fixture uses the real page but not the complete production navigation shell. Restricted database-role deployment verification is a separate gate.

## Supplied 15,000-row durable ingestion measurement

PASS, 2026-11-01, source at `f690055` plus the opt-in `CaseImportLoadTests` harness: all 10,500 Zimbabwe and 4,500 Eswatini records crossed the actual authenticated HTTP route, canonical registration and PostgreSQL import ledger in the disposable local fixture. Separate country workspaces and explicit human/animal grants were provisioned for the synthetic actor. Results: **15,000 newly accepted cases, zero initial replays/rejections, 15,000 unique receipts, 30,000 immutable case events and 30,000 durable domain-outbox rows**. Replaying each country's first 100-row batch after completion produced Replayed receipts without increasing unique counts.

| Measurement | Observed value |
|---|---:|
| Total elapsed including replay/count assertions | 420.879 s |
| Effective serial throughput | 35.64 reports/s |
| Zimbabwe 10,500 rows | 290.466 s |
| Eswatini 4,500 rows | 124.732 s |
| Batches | 150 × 100 reports |
| Batch response p50 / p95 / p99 | 2,727.6 / 3,106.9 / 3,237.0 ms |
| Peak sampled test-host working set | 217.3 MiB |
| Test-host CPU time | 93.77 s |

Environment: Windows .NET 10.0.14 ASP.NET TestServer, PostgreSQL/PostGIS 17 Docker loopback, serial caller, real encryption/transactions; synthetic authentication/policy. Memory includes the test client and server in one process and is sampled after batches; it is not an OS-wide peak. A single mid-run Docker observation showed PostgreSQL at 128.2 MiB and 27.94% CPU; this is not a sustained-resource average. No broker drain or intelligence processing was included. This proves the supplied 15,000-row ingestion path, not 100,000/million-row capacity or multi-node behavior.

Run with `SWIFTAID_RUN_IMPORT_LOAD=1`, `SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES=1`, the disposable connection variable and `SWIFTAID_TEST_CASE_CSV_DIRECTORY`; filter `FullyQualifiedName~CaseImportLoadTests`. Optional `SWIFTAID_IMPORT_LOAD_RESULT` writes measured JSON. Do not point the fixture at a shared database. Later shared-workspace changes require their own regression checks and are not retroactively covered by these numbers.

## Shared-workspace verification

Subsequent broader regression initially found seven failures in the hand-written SQLite membership fixture because it lacked `workspace_organizations`. The three SQLite fixture schemas that create workspaces (membership, invitation, demo data) now include the link table. PASS: all 23 tests across those three classes, including a new real service/query check that both organizations list the shared workspace while a revoked link disappears only from its organization's listing. This is SQLite fixture coverage, separate from the PostgreSQL isolation/migration evidence.

PASS: four real PostgreSQL `SharedWorkspaceImportTests`: separate organizations create cases in the same workspace; case reads and import history remain isolated; an organization change cannot replay a command into another organization; a link alone grants no membership; link revocation waits for an active authority transaction and denies new work afterward. Migration applied successfully over the populated disposable database.

PASS: expanded Chromium integration selects a secondary organization, imports a case into the same workspace and restores its result after refresh, in addition to the 250-row pause/resume and invalid-row scenario. The database asserts the actual selected organization on the saved case. Browser interaction 12,649 ms, no page errors. Same synthetic authentication/policy and real component/database boundaries as above; shared Gateway/native devices remain NOT VERIFIED.

PASS: complete Application suite: 605 passed, 3 existing live-provider skips. Integration filter `CaseRegistration|CaseAuthority|CaseImportPostgresTests|PostgresCaseCommand|Notification`: 52 passed, 25 opt-in SQLAUTH fixtures skipped. Those skips are NOT VERIFIED, not evidence of passing the restricted-role or historical migration suites. Updated case-reader grants and the audited link script ran successfully in the disposable PostgreSQL container. Restricted registration/import runtime validation remains a separate check.

PASS (subsequent check): `CaseImportRuntimeTests`, real non-owner PostgreSQL login using the reviewed import extension. Registers a case for a secondary organization, replays the same command without duplication and proves permission-denied responses for link/membership changes, protected source/context changes, row deletion and outbox publication updates. Before the correction this test failed with registration-unavailable; the database reported missing lock-function EXECUTE. The corrected profile includes only required canonical case/notification inserts and ledger rights. The command profile also includes new outbox metadata columns in its INSERT allowlist, preserving existing command persistence after the earlier outbox migration. Broader SQLAUTH fixtures remain skipped; this result does not relabel them as verified.

## Import fault and boundary regressions

PASS: two explicit-opt-in `IngestionRestartTests` killed the labelled disposable PostgreSQL and NATS processes with SIGKILL and restarted their containers. A committed HTTP import survives PostgreSQL recovery, replays its first row and resumes the next through a recreated HTTP host, preserving exactly two receipts, four events and four outbox rows. A real JetStream PubAck survives broker process loss; republishing the same notice returns the same stored sequence. Tests finished in about 5 seconds. This is actual local database/broker process loss, not host power loss, replica failover, a separate Gateway OS-process restart, long network partition or downstream consumer completion.

PASS: `CaseImportMetricsTests` observes real accepted/replayed imports with the .NET MeterListener, verifies all five stage histograms and exact committed outcome counters, and asserts each observation has only its bounded stage/status label. Two functional batch samples were 554.92 and 125.92 ms; stage sample ranges: admission 14.02–119.48 ms, validation 0.03–0.12 ms, canonical registration 72.13–286.80 ms, checkpoint 5.75–13.09 ms. This cold/warm functional sample is not a percentile, sustained performance result or external telemetry-export proof.

PASS: Ellai import-monitoring extension, 298 Ellai application tests (3 existing provider skips) and one real broker/PostgreSQL integration scenario. The broker reads current permitted destinations, recent owner imports and durable results, and denies another owner, stale epoch, injected actor argument, missing actor, exhausted budget and excluded public-help profile. No raw narrative is returned. Catalogue and canonical-reference tests were updated to cover the three registered staff tools. Provider-driven selection, client TTS and live account authorization remain NOT VERIFIED.

PASS: ten PostgreSQL/HTTP tests (`CaseImportEndpointTests|CaseImportPostgresTests`) after two reproduced failures and focused corrections. Before correction, a Content-Length-free body exceeding 8 MiB returned HTTP 200; after correction it returns 413 and retains the accepted first case plus a paused durable checkpoint. Before correction, a throwing policy-validation dependency produced no row outcome and left Processing; after correction it records four RetryableFailure attempts, a fifth Failed outcome, and successful explicit reprocessing after recovery with one canonical receipt. Existing commit-before-checkpoint, authorization, changed-source, lease and restored-history tests remain passing. No shared service or patient data was used.
