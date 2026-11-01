# Gateway ingestion verification and performance report

2026-11-01. Synthetic, disposable local Docker fixtures on Windows; .NET 10.0.14, PostgreSQL/PostGIS 17, NATS 2.15.1. No patient data.

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

NOT VERIFIED: 10,000/100,000/1,000,000/multi-million full ingestion and processing runs; sustained throughput, p95/p99 end-to-end latency, CPU/working-set limits, multi-node HA, actual process/network restart, full consumer result-before-ACK, statistical detection accuracy, physical devices and external providers. Stream capacity settings are limits, not measured throughput.

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
