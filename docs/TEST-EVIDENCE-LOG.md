# Test and review evidence log — 2 November 2026

## Scope and ownership

This log is the current checklist-run index. Each entry distinguishes source/document checks from builds, tests, provider/device checks and product acceptance. `docs/ACCEPTANCE.md` remains the scenario-level register; `docs/TRACEABILITY.md` remains the requirement/task crosswalk; `docs/BLOCKERS.md` assigns stable `BL-*` issue IDs.

The master orchestrator owns checklist status, evidence linking and regression coordination. Code-level regression owners are not reassigned by this document: `docs/TASK-OWNERS.md` is a historical 27 October snapshot and current path ownership must be confirmed from active handoffs before shared code, contract, migration, package-lock or generated-file edits. No individual is inferred as the owner of an unresolved failure.

## Checklist baseline evidence

| ID | Check / command | Result and scope | Issue / evidence |
|---|---|---|---|
| EV-001 | `dotnet test SwiftAid.sln --no-restore -m:1 -nr:false --verbosity quiet` | PARTIAL/FAIL: Domain 83 passed; Application 867 passed, 3 skipped; Integration startup failures were corrected in `5489f2c`; Client 293 passed; UI 520 passed, 7 failed; solution run also lacked iOS `GoogleDataTransport` assets. | BL-002, [baseline audit](BASELINE-AUDIT.md), [acceptance](ACCEPTANCE.md) |
| EV-002 | `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'` | PASS after endpoint fix: 602 passed, 0 failed, 226 skipped. Provider/database-gated cases remain unverified. | [baseline audit](BASELINE-AUDIT.md) |
| EV-003 | Focused `EllaiPrivilegedRouteAuthorizationTests` command recorded in `docs/BASELINE-AUDIT.md` | PASS post-commit: 20 passed, 0 failed, 0 skipped. | Commit `5489f2c20ef62d8b9033d901358b943baf4de4a4` |
| EV-004 | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -c Release -f net10.0-android --no-restore -m:1 -nr:false -p:EmbedAssembliesIntoApk=true --verbosity quiet` | PASS: 0 warnings/errors. Android device behavior NOT VERIFIED. | [baseline audit](BASELINE-AUDIT.md) |
| EV-005 | `uv run --project workers --locked pytest workers/tests -q` | PASS: 59 passed, 3 skipped. | [baseline audit](BASELINE-AUDIT.md) |
| EV-006 | `docker compose --profile local-bootstrap -f infra/compose.yml config --format json` | PASS: configuration parse only; services and providers NOT VERIFIED by this command. | [baseline audit](BASELINE-AUDIT.md) |
| EV-007 | `node tests/SwiftAid.Browser.Tests/guest-storage.browser.cjs` | NOT VERIFIED: Playwright module unavailable in local Node environment. | BL-003 |
| EV-008 | `git diff --check`, source hash/byte recomputation, JSON parse, SA-ID coverage comparison | PASS for documentation slices: JSON parses; 379 unique source SA IDs map to 379 checklist rows; hashes/byte counts recorded in `DOCUMENT-INTEGRITY.md`. | Commit `50093ad` |
| EV-009 | Source policy/gate inspection for SW-00-006/007 | PASS for source-level policy/defaults only. Deployed configuration, rendered UI and end-to-end runtime enforcement NOT VERIFIED. | [safety audit](SAFETY-POLICY-AUDIT.md), [inference gate audit](INFERENCE-GATE-AUDIT.md) |
| EV-010 | Official model-card and tracked-asset inventory review for SW-00-008 | PARTIAL/BLOCKED: model-card restrictions recorded; consent and several asset/source rights remain unresolved. | BL-008, [rights review](RIGHTS-REVIEW-2026-11-02.md) |
| EV-011 | `dotnet restore SwiftAid.sln --locked-mode -m:1 -nr:false` | PASS: all solution projects restored from locked dependencies; previous local iOS package-file failure no longer reproduces. | SW-00-004, [baseline audit](BASELINE-AUDIT.md) |
| EV-012 | `dotnet test SwiftAid.sln --no-build --no-restore -m:1 -nr:false --verbosity quiet` | PASS, exit 0: Domain 83, Application 867/3 skipped, Integration 602/226 skipped, Client 293, UI 527; 2,372 passed / 229 skipped / 0 failed. | SW-00-004, [acceptance](ACCEPTANCE.md) |
| EV-013 | `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'` | PASS: UI suite 527/527 after focused test-fixture corrections. | Commit `b97f87c` |
| EV-014 | Local Compose service readiness requests before/after Gateway restart | PASS for Gateway liveness, PostgreSQL readiness, MinIO, NATS, Redis, and Gateway links to language/TTS/Qdrant; see exact commands in [runtime record](SW-01-002-RUNTIME.md). These checks do not establish provider inference or full workflows. | SW-01-002 |
| EV-015 | `docker compose -f infra/compose.yml restart gateway` | FAIL, exit 1: Gateway restarted and became healthy; Compose then failed restarting the stopped optional classification worker due to a missing Gateway network namespace. Worker remains stopped. | BL-009, [runtime record](SW-01-002-RUNTIME.md) |
| EV-016 | `dotnet package list --project SwiftAid.sln --vulnerable --include-transitive --no-restore --format json` | PASS, exit 0; 16 projects enumerated, no vulnerable packages listed. Snapshot scope only; full supply-chain/license review NOT VERIFIED. | SW-01-003, [pin audit](SW-01-003-PIN-AUDIT.md) |
| EV-017 | `uv lock --check` (working directory `workers/`) | PASS; resolved 54 packages, lock consistent. | SW-01-003, [pin audit](SW-01-003-PIN-AUDIT.md) |
| EV-018 | `npm ls --package-lock-only --depth=0` (repository root) | PASS; four declared direct packages present in the lock. Playwright absent. | SW-01-003 / BL-003, [pin audit](SW-01-003-PIN-AUDIT.md) |

## Active regression and blocker ownership

| Issue | Failure / gap | Current owner | Reopen/closure evidence |
|---|---|---|---|
| BL-001 | 16 source attachments unavailable for comparison | User/source provider for missing material; orchestrator tracks | Supply the source documents, then complete SW-00-002 comparison |
| BL-002 | Seven UI baseline failures and missing iOS NuGet assets | Unassigned; confirm from current handoff before code edits | Repair package cache/restore; diagnose UI cases; rerun focused suites and full baseline |
| BL-003 | Browser harness cannot load Playwright | Unassigned; confirm package/test ownership | Provision approved browser-test environment and run guest-storage workflow |
| BL-008 | Recording consent plus model/data/asset rights incomplete | Rights owner not recorded; orchestrator tracks evidence | Add source, license, provenance and consent/approval evidence per `RIGHTS-REGISTER.md` |

Historical IDs in `docs/BLOCKERS.md` remain valid where present. New IDs must be unique and linked to a checklist item; do not recycle a closed ID. A documentation status never substitutes for its required runtime test or independent review.
