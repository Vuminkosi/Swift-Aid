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
| `dotnet test SwiftAid.sln --no-restore -m:1 -nr:false --verbosity quiet` | Partial/fail: Domain 83 passed; Application 867 passed, 3 skipped; Integration initially 20 failed at Gateway host startup; Client 293 passed; UI 520 passed, 7 failed. The solution then reported missing `GoogleDataTransport` iOS package files (`MSB3030`). |
| `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'` | After endpoint correction: 602 passed, 0 failed, 226 skipped. |
| `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~EllaiPrivilegedRouteAuthorizationTests' --logger 'console;verbosity=minimal'` | Post-commit: 20 passed, 0 failed, 0 skipped. |
| `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -c Release -f net10.0-android --no-restore -m:1 -nr:false -p:EmbedAssembliesIntoApk=true --verbosity quiet` | PASS: 0 warnings, 0 errors. No Android device install or interaction was performed. |
| `uv run --project workers --locked pytest workers/tests -q` | PASS: 59 passed, 3 skipped. |
| `docker compose --profile local-bootstrap -f infra/compose.yml config --format json` | PASS: configuration parsed; no workflow claim. |
| `node tests/SwiftAid.Browser.Tests/guest-storage.browser.cjs` | NOT VERIFIED: the local Node environment lacks the `playwright` module. |

The seven UI failures are retained: three `CaseReportDisplayTests` assertions target output that differs from current `CaseReportEvidence.razor` wording/format; four `CaseDetailWorkActionRefreshTests` fail in the test renderer with `ArgumentNullException` for `jsObjectReference`. These require separate diagnosis; they were not changed as part of the endpoint correction.

## Supplied-document reconciliation

The supplied attachment directory contains the two execution-directive text files. It does not contain the 16 product/source documents referenced by the checklist. Current repository documents were inspected; stale v1.4/v2.4 references remain in historical body text and are flagged for reconciliation. Current authority notes and source versions are recorded in acceptance, traceability, capability and task-ownership documents. Full comparison of the unavailable 16 documents remains blocked. Source hashes at the start of this audit: `spec.md` SHA-256 `67CEA198BAD51ABBB1B457A4A869E088A8B206521E6B67C430D0C2B8733ED365`; `implementation.md` SHA-256 `887F78CB23AFB07F41EA6222A179DB609BBD36F348A85782A6A3656BB48FE5DC`.

## Status

- `SW-00-001`: Passed — repository, source versions, runtime, branch, commit, worktree, migration count and route-source inventory recorded.
- `SW-00-002`: Blocked — missing 16 supplied source documents; remaining repository-wide stale records still require reconciliation.
- `SW-00-004`: Failed/partial — see exact baseline results above.
- `SW-01-004`: In progress — the focused Gateway audio route binding defect is corrected; the complete versioned command/event/audio schema inventory remains open.

## Evidence sources

- `AGENTS.md`, `spec.md`, `implementation.md`, `global.json`, `SwiftAid.sln`, `infra/compose.yml`
- `docs/PROGRESS.md`, `docs/ACCEPTANCE.md`, `docs/CAPABILITIES.md`, `docs/TRACEABILITY.md`, `docs/TASK-OWNERS.md`
- `src/SwiftAid.Gateway/Program.cs`, `src/SwiftAid.Gateway/Endpoints/EllaiGuestEndpoints.cs`, `src/SwiftAid.Infrastructure/Persistence/Migrations/`
- Command output from `git`, `dotnet`, `uv`, `docker compose`, and the focused Node browser-harness attempt as listed above
