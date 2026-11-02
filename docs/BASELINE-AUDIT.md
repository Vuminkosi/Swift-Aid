# SwiftAid baseline inventory — 2 November 2026

## Repository and execution context

- Canonical checkout: `C:\DevOps\SwiftAidv2`; active branch: `interactive-ellai`.
- Baseline before this turn: `865964dbe9664a557fd0f0e303af03e115f22839`. Corrective commit: `5489f2c20ef62d8b9033d901358b943baf4de4a4`.
- `git worktree list --porcelain` reported one worktree, the canonical checkout. No pre-existing staged or unstaged changes were present.
- Root contract pair: `spec.md` v1.5 and `implementation.md` v2.5. SDK pinned by `global.json`: .NET 10.0.403. `workers/pyproject.toml` requires Python 3.12; the host reports Python 3.14.4. Compose CLI: v5.6.1.
- Git history at baseline starts with `865964d` ("Ignore checklist.md"), followed by recent Ellai intake, Qdrant and public-help changes. The root checklist is ignored by `.gitignore`; it is explicitly tracked for this user-requested execution and is not a generated build artifact.
- `docs/TASK-OWNERS.md` is dated 27 October and has stale assignment rows. Treat the master orchestrator as global owner; obtain current handoff ownership before shared contract, migration or package-lock changes.

## Runtime, routes and persistence inventory

- The running Gateway belongs to this repository's `swiftaid` Compose project, is healthy, and publishes `127.0.0.1:8080`; its image ID is `sha256:606533c35fee5e1dd08ac763e58058011fef39394a91eaa7664aacf083f82919`. The image does not identify the source commit, so no source-to-image equivalence is claimed.
- Eight SwiftAid Compose services were listed: Gateway, PostgreSQL/PostGIS, MinIO, NATS, Redis, Qdrant, Ellai language and Ellai TTS. Seven reported healthy; Qdrant was `Up` without a health status in `docker compose ps`. Service presence is not proof of feature readiness.
- A separate container named `redis` belongs to `C:\DevOps\Java\system-images` / Compose project `core-banking-sim`, not SwiftAid. Its restart logs report malformed AOF data. No action was taken against it or its data.
- Source search found 160 route-related declarations/attributes under `src`; this is a search count, not a normalized endpoint inventory. `src/SwiftAid.Infrastructure/Persistence/Migrations` contains 45 migration source files and 45 generated designer files (90 files total).
- Canonical API/application surfaces are in `src/SwiftAid.Gateway/Endpoints`, `src/SwiftAid.Application`, `src/SwiftAid.Contracts`, and `src/SwiftAid.Infrastructure`. Gateway host registration is `src/SwiftAid.Gateway/Program.cs`; the route-binding defect fixed in this run was in `Endpoints/EllaiGuestEndpoints.cs`.
- Qdrant exact-reference adapter/projection source exists and is wired in Gateway. Current live projection/recall was not exercised in this audit. PostgreSQL remains authoritative per repository policy.

## Baseline checks and evidence

All commands below ran from the canonical checkout on .NET SDK 10.0.403 unless stated otherwise.

| Command | Result |
|---|---|
| `dotnet restore SwiftAid.sln --locked-mode -m:1 -nr:false` | PASS: all solution projects restored using locked dependencies. This resolved the earlier local `GoogleDataTransport` package-file failure. |
| `dotnet test SwiftAid.sln --no-build --no-restore -m:1 -nr:false --verbosity quiet` | PASS, exit 0: Domain 83; Application 867 passed / 3 skipped; Integration 602 passed / 226 skipped; Client 293; UI 527. Total 2,372 passed / 229 skipped / 0 failed. |
| Initial `dotnet test SwiftAid.sln --no-restore -m:1 -nr:false --verbosity quiet` | Historical partial/fail before test-harness corrections: Domain 83 passed; Application 867 passed, 3 skipped; Integration initially 20 failed at Gateway host startup; Client 293 passed; UI 520 passed, 7 failed. The initial run also lacked local `GoogleDataTransport` iOS package files (`MSB3030`). |
| `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'` | PASS after test-fixture repair: 527 passed, 0 failed. Focused display/renderer cases passed 9/9. |
| `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'` | After endpoint correction: 602 passed, 0 failed, 226 skipped. |
| `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~EllaiPrivilegedRouteAuthorizationTests' --logger 'console;verbosity=minimal'` | Post-commit: 20 passed, 0 failed, 0 skipped. |
| `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -c Release -f net10.0-android --no-restore -m:1 -nr:false -p:EmbedAssembliesIntoApk=true --verbosity quiet` | PASS: 0 warnings, 0 errors. No Android device install or interaction was performed. |
| `uv run --project workers --locked pytest workers/tests -q` | PASS: 59 passed, 3 skipped. |
| `docker compose --profile local-bootstrap -f infra/compose.yml config --format json` | PASS: configuration parsed; no workflow claim. |
| Initial `node tests/SwiftAid.Browser.Tests/guest-storage.browser.cjs` | Initially NOT VERIFIED because Node did not resolve the repository-local Playwright package. Refreshed verification below passes after setting `NODE_PATH` to the existing package; no package was added. |

The three display failures were stale assertions for distance units, locality wording and capitalization; they now assert the current accessible output. Four renderer failures came from the test `IJSRuntime` returning null for `IJSObjectReference`; its no-op reference now supports the MudBlazor rendering path. The UI suite and the previous solution test run passed after this test-only correction. The refreshed current-run results and iOS packaging limitation are recorded below.

## Supplied-document reconciliation

The supplied attachment directory contains the two execution-directive text files. It does not contain the 16 product/source documents referenced by the checklist. Current repository documents were inspected; stale v1.4/v2.4 references remain in historical body text and are flagged for reconciliation. Current authority notes and source versions are recorded in acceptance, traceability, capability and task-ownership documents. Full comparison of the unavailable 16 documents remains blocked. Source hashes at the start of this audit: `spec.md` SHA-256 `67CEA198BAD51ABBB1B457A4A869E088A8B206521E6B67C430D0C2B8733ED365`; `implementation.md` SHA-256 `887F78CB23AFB07F41EA6222A179DB609BBD36F348A85782A6A3656BB48FE5DC`.

## Status

- `SW-00-001`: Passed — repository, source versions, runtime, branch, commit, worktree, migration count and route-source inventory recorded.
- `SW-00-002`: Blocked — missing 16 supplied source documents; remaining repository-wide stale records still require reconciliation.
- `SW-00-004`: Partial — locked restore and all .NET test assemblies pass; Python, Android Release and Compose checks pass as recorded; browser harness remains NOT VERIFIED because Playwright is unavailable.
- `SW-01-004`: In progress — the focused Gateway audio route binding defect is corrected; the complete versioned command/event/audio schema inventory remains open.

## Evidence sources

- `AGENTS.md`, `spec.md`, `implementation.md`, `global.json`, `SwiftAid.sln`, `infra/compose.yml`
- `docs/PROGRESS.md`, `docs/ACCEPTANCE.md`, `docs/CAPABILITIES.md`, `docs/TRACEABILITY.md`, `docs/TASK-OWNERS.md`
- `src/SwiftAid.Gateway/Program.cs`, `src/SwiftAid.Gateway/Endpoints/EllaiGuestEndpoints.cs`, `src/SwiftAid.Infrastructure/Persistence/Migrations/`
- Command output from `git`, `dotnet`, `uv`, `docker compose`, and the focused Node browser-harness attempt as listed above

## Refreshed baseline checks — 2 November 2026

The following checks were rerun on branch `interactive-ellai` after commit `8b4a07dfbd3f4188408b51fc1fbacc091998bee2`.

| Check | Result |
|---|---|
| `dotnet test SwiftAid.sln --no-build --no-restore -m:1 -nr:false -p:WasmBuildNative=false -p:TargetFrameworks=net10.0-android --verbosity quiet` | PASS, exit 0: Domain 104; Application 895 passed / 3 skipped; Integration 689 passed / 239 skipped; Client 326; UI 542. Total 2,556 passed / 242 skipped / 0 failed. |
| Unrestricted `dotnet test SwiftAid.sln --no-restore -m:1 -nr:false -p:WasmBuildNative=false --verbosity quiet` | FAIL: iOS target build cannot copy `GoogleDataTransport` XCFramework privacy resources and headers from the local NuGet cache (MSB3030). Locked restore completed but did not repair those missing package files. Android Release target was separately built successfully. |
| `workers/.venv/Scripts/python.exe -m pytest workers/tests -q` | PASS: 59 passed, 3 skipped. Environment uses repository Python venv. |
| `$env:NODE_PATH=(Resolve-Path '.codex/case-list-browser/node_modules').Path; node tests/SwiftAid.Browser.Tests/guest-storage.browser.cjs` | PASS: all 6 focused guest-storage browser checks. |
| `node tests/SwiftAid.Ui.Tests/case-assistance-request.browser.cjs` with the local Playwright package | PASS: synthetic assistance request, receipt, unmet-need, and follow-up acknowledgement at 390px and 1280px. |
| `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -c Release --no-restore -f net10.0-android -m:1 -nr:false --verbosity quiet` | PASS: 0 warnings, 0 errors. No device install or physical-device interaction was performed. |
| `docker compose -f infra/compose.yml config -q` | PASS: Compose configuration parses. This is configuration validation only. The previously recorded Compose restart failure remains under SW-01-002; no restart was attempted during this pass. |

SW-00-004 baseline capture is complete with the iOS package failure and Compose runtime limitation explicitly recorded. Browser harness availability is verified through the existing local Playwright package; no repository dependency was added. Evidence log entry: EV-057.
