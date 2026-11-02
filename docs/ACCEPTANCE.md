# Product acceptance register

Current authority is spec v1.5 and implementation v2.5. The scenario inventory below is retained pending reconciliation to this pair; the prior 76-scenario count belongs to v1.4/v2.4.

Source of truth: spec.md v1.5, sections 26-27; implementation.md v2.5, section 18.4 and task contracts. Snapshot: 2 November 2026. The current product specification defines scenarios through AT-86; existing status entries do not constitute execution evidence.

The source-alignment note below is historical v1.4/v2.4 context; acceptance scope is governed by the current v1.5/v2.5 source pair.

**Evidence rule:** each result must name the test/device/provider, command or procedure, source revision, output/evidence path, and verdict. A mock or OFF-gate result cannot stand in for live provider, Android, licensing, rights, professional, or positive optional-feature evidence.

## Current baseline run

Repository baseline `865964dbe9664a557fd0f0e303af03e115f22839`, branch `interactive-ellai`; focused endpoint correction committed as `5489f2c20ef62d8b9033d901358b943baf4de4a4`. Full commands and limitations are recorded in [BASELINE-AUDIT.md](BASELINE-AUDIT.md).

| Check | Result |
|---|---|
| Domain/Application/Client suites | PASS in the solution baseline: Domain 83, Application 867 passed and 3 skipped, Client 293. |
| Full solution test run | PASS, exit 0 with `--no-build --no-restore`: 2,372 passed, 229 skipped, 0 failed across Domain, Application, Integration, Client and UI. |
| Full Integration suite after correction | PASS: 602 passed, 0 failed, 226 skipped. |
| Focused Ellai authorization suite after commit | PASS: 20 passed, 0 failed, 0 skipped. |
| UI suite after test-fixture repair | PASS: 527 passed, 0 failed. |
| Android Release build | PASS: 0 warnings, 0 errors; device execution NOT VERIFIED. |
| Python worker suite | PASS: 59 passed, 3 skipped. |
| Compose configuration | PASS: parse only; provider workflows NOT VERIFIED. |
| Locked solution restore | PASS: `dotnet restore SwiftAid.sln --locked-mode -m:1 -nr:false`; earlier missing iOS package files resolved. |
| Browser guest-storage harness | NOT VERIFIED: local Playwright package unavailable. |

| ID | Scenario | Status |
|---|---|---|
| AT-01 | Provisioned offline capture | NOT RUN |
| AT-02 | First-run guest / staff separation | NOT RUN |
| AT-03 | Animal group reporting | NOT RUN |
| AT-04 | No diagnosis required | NOT RUN |
| AT-05 | Multi-person narrative | NOT RUN |
| AT-06 | Commit then timeout | NOT RUN |
| AT-07 | Concurrent correction | NOT RUN |
| AT-08 | Immutable facts | NOT RUN |
| AT-09 | Scoped access | NOT RUN |
| AT-10 | Sensitive human case | NOT RUN |
| AT-11 | Reassignment and old record | NOT RUN |
| AT-12 | Offline grant expires | NOT RUN |
| AT-13 | Original voice survives | NOT RUN |
| AT-14 | ASR fails | NOT RUN |
| AT-15 | Audio permission revoked | NOT RUN |
| AT-16 | Transcript correction | NOT RUN |
| AT-17 | Assistance gap | NOT RUN |
| AT-18 | Missed follow-up | NOT RUN |
| AT-19 | Escalation without AI | NOT RUN |
| AT-20 | Closure with open work | NOT RUN |
| AT-21 | Reopening | NOT RUN |
| AT-22 | Hostile narrative | NOT RUN |
| AT-23 | Stale AI proposal | NOT RUN |
| AT-24 | Missing reference/evidence | NOT RUN |
| AT-25 | Withdrawn education | NOT RUN |
| AT-26 | Delayed batch / gated inference | NOT RUN |
| AT-27 | Human/animal relationship gate | NOT RUN |
| AT-28 | Optional signal corrections / gate | NOT RUN |
| AT-29 | Session without social API | NOT RUN |
| AT-30 | Manual share vs delivery | NOT RUN |
| AT-31 | Cancelled reminder | NOT RUN |
| AT-32 | Public privacy | NOT RUN |
| AT-33 | Queue retry/restart | NOT RUN |
| AT-34 | Budget exhausted | NOT RUN |
| AT-35 | Seed coverage | NOT RUN |
| AT-36 | Release rehearsal | NOT RUN |
| AT-37 | Advice refused even with approval | NOT RUN |
| AT-38 | Unapproved plan | NOT RUN |
| AT-39 | Stale or expired approval | NOT RUN |
| AT-40 | Duplicate action and false completion | NOT RUN |
| AT-41 | Partial run | NOT RUN |
| AT-42 | No justified action | NOT RUN |
| AT-43 | Public/new-work/rights audit | NOT RUN |
| AT-44 | Event timeline and video | NOT RUN |
| AT-45 | Optional open-weight proof | NOT RUN |
| AT-46 | No medical-generation alias | NOT RUN |
| AT-47 | Optional promotion truthfulness | NOT RUN |
| AT-48 | Meaningful agentic effect | NOT RUN |
| AT-49 | Guest human report | NOT RUN |
| AT-50 | Global animal report | NOT RUN |
| AT-51 | Geography and time precision | NOT RUN |
| AT-52 | Guest timeout retry | NOT RUN |
| AT-53 | Reviewer promotion race | NOT RUN |
| AT-54 | Uncovered intake | NOT RUN |
| AT-55 | Receipt enumeration | NOT RUN |
| AT-56 | Guest upload and abuse limits | NOT RUN |
| AT-57 | Fresh-install offline guest | NOT RUN |
| AT-58 | Guest to signed-in account | NOT RUN |
| AT-59 | Actual social sign-in | NOT RUN |
| AT-60 | Organizational SSO isolation | NOT RUN |
| AT-61 | Provider linking attack | NOT RUN |
| AT-62 | Native and web flow security | NOT RUN |
| AT-63 | Revoked membership | NOT RUN |
| AT-64 | Event subscription login boundary | NOT RUN |
| AT-65 | Subscription cancellation race | NOT RUN |
| AT-66 | Qdrant scope isolation | NOT RUN |
| AT-67 | Memory poisoning | NOT RUN |
| AT-68 | Exact cache freshness | NOT RUN |
| AT-69 | Expired memory delayed cleanup | NOT RUN |
| AT-70 | Embedding unavailable | NOT RUN |
| AT-71 | Qdrant outage and guest isolation | NOT RUN |
| AT-72 | Erasure versus replay/restore | NOT RUN |
| AT-73 | Multi-turn factual clarification | NOT RUN |
| AT-74 | Interruption, restart and stale context | NOT RUN |
| AT-75 | Approval/receipt and loop budget | NOT RUN |
| AT-76 | Memory and tenant isolation across turns | NOT RUN |
| AT-77 | Microphone denied | NOT RUN |
| AT-78 | Voice Q&A and duplicate finalized turns | NOT RUN |
| AT-79 | Barge-in and checkpoint revisit | NOT RUN |
| AT-80 | Cancellation with queued audio | NOT RUN |
| AT-81 | Authentication or consent revoked mid-speech | NOT RUN |
| AT-82 | Speech provider, network, or budget outage | NOT RUN |
| AT-83 | TTS checkpoint rights gate | NOT RUN |
| AT-84 | Web/Android PCM conformance | NOT RUN |
| AT-85 | Speech is not action proof | NOT RUN |
| AT-86 | Accessible conversation | NOT RUN |

P8.6 evidence remains NOT VERIFIED for AT-73–AT-76. The source requires separate model turns, durable checkpoint/restart behavior, stale/version and permission checks, approval/receipt idempotency, tenant-scoped memory, bounded cycles, and one real supported Meta multi-turn smoke with synthetic case data. Simulated calls and real provider calls must be identified separately. No P8.6 smoke or behavioral test result is recorded here.
## Mandatory release boundaries

- P0 release acceptance is not achieved by scaffolding or by a passing build alone.
- Preserve the account-free guest intake and staff authorization boundary.
- Clinical/veterinary diagnosis, triage, treatment advice, and generated health teaching remain excluded.
- Cross-sector inference remains off until its separate product decision and validation are recorded.
- Before any release claim, resolve applicable privacy/security, real device, integration, source-rights, accessibility, and independent review evidence in the root specification and plan.

SW-05-002 implementation follow-up: Mobile preserves recorded sector/classification and filters before pagination using authorized queue fields. Unknown sectors remain Unknown. Focused MobileCaseWorkspaceTests PASS 7/7; full Client suite PASS 300/300, zero skipped. See docs/SW-05-002-CASE-WORKSPACE-AUDIT.md for commands. Browser/Android and live authorization NOT VERIFIED; checklist remains partial.

SW-05-002 web filter follow-up: CaseWorkspaceGatewayClient now derives sector and recorded-category choices from its authorized queue and applies both filters before pagination. Unknown source sectors remain Unknown; denied/unavailable results expose neither rows nor options. Tests: dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~CaseWorkspaceGatewayTests|FullyQualifiedName~CaseQueueClientScopeTests" --verbosity quiet. PASS 7/7, zero skips. Build emitted WASM0001 SQLite varargs warnings. Browser/device/live database behavior NOT VERIFIED. The 100-case cap, full pagination, owner/activity/next-action gaps remain open; SW-05-002 remains partial. This supersedes the earlier audit statement that Gateway sector/category filters are unsupported.

SW-05-002 sector UI integration correction: dropdown option parsing now accepts the Human/Animal casing supplied by both workspace clients, explicitly parses Unknown, and shares that parser with Ellai's validated current-option handling. Gateway and Mobile page adapters preserve Unknown as a filter instead of clearing it. Focused command: dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter "FullyQualifiedName~EllaiCaseFilterTests|FullyQualifiedName~EllaiCaseListSemanticsTests" --verbosity quiet (PASS 20/20). Regression command: dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-build --no-restore -m:1 -nr:false --verbosity quiet (PASS 532/532, zero skips). Browser and Android device interaction NOT VERIFIED; SW-05-002 remains partial.

SW-05-002 compact filter access: OperationsCaseQueue now provides a More filters/Hide filters toggle below 1280px, exposing the existing capability-gated filter grid and sort controls without duplicating their handlers. The button declares aria-expanded and aria-controls; desktop retains the full grid. Regression: dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --verbosity quiet; PASS 532/532, zero failures/skips. This is component build/regression evidence only; the new toggle's browser/touch/keyboard and Android behavior remain NOT VERIFIED. SW-05-002 remains partial.

SW-05-002 compact-filter interaction evidence: added a render-tree test in AuthorizedLayoutNavigationAccessibilityTests that invokes the actual MudButton callback twice, checks false/true/false aria-expanded, its aria-controls target, collapsed/expanded grid classes, and unchanged query. Command: dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~AuthorizedLayoutNavigationAccessibilityTests --verbosity quiet. PASS 9/9, zero skips. This verifies component interaction with the existing fake viewport harness; CSS layout, browser touch/keyboard and Android device acceptance remain NOT VERIFIED.

SW-05-002 pagination arithmetic correction: Gateway and Mobile case workspace projections calculate offsets/HasMore with 64-bit arithmetic and clamp Skip to the filtered row count. Previously a very large valid page number could overflow int arithmetic and return early rows with incorrect HasMore. New tests request int.MaxValue at size 100 and assert empty items, retained total and HasMore=false. PASS 8/8: dotnet test tests/SwiftAid.Client.Tests/SwiftAid.Client.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~MobileCaseWorkspaceTests --verbosity quiet. PASS 8/8: dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter "FullyQualifiedName~CaseWorkspaceGatewayTests|FullyQualifiedName~CaseQueueClientScopeTests" --verbosity quiet. Zero failures/skips. This fixes arithmetic only; complete server-side pagination beyond queue caps and browser/device acceptance remain unverified. SW-05-002 remains partial.

SW-05-002 stable page membership: Gateway and Mobile workspace projections now break UpdatedAtUtc ties by CaseId, matching the persistence query's explicit identity tie-breaker. Tests reorder two equal-time input rows and request separate pages in both sort directions; page membership and HasMore stay stable. PASS 10/10: dotnet test tests/SwiftAid.Client.Tests/SwiftAid.Client.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~MobileCaseWorkspaceTests --verbosity quiet. PASS 10/10: dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter "FullyQualifiedName~CaseWorkspaceGatewayTests|FullyQualifiedName~CaseQueueClientScopeTests" --verbosity quiet. Zero skips. Complete server continuation remains open: PostgresCaseQueueStore limits candidates before CaseQueueQuery performs per-case policy checks, so visible row count cannot prove exhaustion. Continuation must advance using candidate position independently of denied rows and preserve per-case authorization without exposing hidden records. Current capped clients and browser/device acceptance remain partial.

SW-05-002 Mobile reference/owner parity: MobileCaseWorkspaceClient now uses the existing web Case + eight-character ID display convention, supports trimmed case-insensitive reference search, and labels rows from its owned queue Assigned to you without exposing the raw owner identifier. Compact shared case cards now display Owner with Not recorded fallback. Full Client tests PASS 305/305 via dotnet test tests/SwiftAid.Client.Tests/SwiftAid.Client.Tests.csproj --no-restore -m:1 -nr:false --verbosity quiet. Focused UI tests PASS 29/29 via dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter "FullyQualifiedName~AuthorizedLayoutNavigationAccessibilityTests|FullyQualifiedName~EllaiCaseFilterTests|FullyQualifiedName~EllaiCaseListSemanticsTests" --verbosity quiet. Browser/device visual acceptance NOT VERIFIED; reference labels are presentation aids, not unique authority tokens. Server continuation/activity/next-action gaps remain open; SW-05-002 remains partial.

SW-05-002 browser evidence: added CaseQueueFixture.razor and case-queue-filters.browser.cjs using the synthetic UI host. PASS Chromium at 390/1280px: Enter opens/closes compact filter grid, aria-expanded changes, Unknown selection updates the query, desktop grid remains visible and no horizontal overflow/page errors occur. Initial harness failures (interactive readiness/popover setup and ambiguous selector) were corrected before the passing run. Build: dotnet build tests/SwiftAid.Ui.TestHost/SwiftAid.Ui.TestHost.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --verbosity quiet (0 warnings/errors). Browser command: set SWIFTAID_PLAYWRIGHT_PACKAGE to .codex/case-list-browser/node_modules/playwright absolute path, then node tests/SwiftAid.Ui.Tests/case-queue-filters.browser.cjs (exit 0). Runner installed with npm install --prefix .codex/case-list-browser --no-audit --no-fund playwright. Test host/browser closed in finally. Task-local runner retained because automatic approval review rejected cleanup as blocked by policy. CUA failed Windows sandbox initialization; Playwright fallback succeeded. Live Gateway revision was not established; live authorization and Android remain NOT VERIFIED. SW-05-002 remains partial.

SW-05-003 rendered tab verification: expanded existing EllaiCaseTabsFixture and ellai-case-tabs.browser.cjs to cover Overview, History, Follow-ups, Assistance, Discussion, Attachments and Observations at 1280/390px. PASS route selection, active semantic observations, private-value exclusion, reload restoration, keyboard Enter activation and no horizontal overflow. Initial failures were harness timing (same-tab navigation and post-reload prerender); explicit URL/readiness waits resolved them. Build command: dotnet build tests/SwiftAid.Ui.TestHost/SwiftAid.Ui.TestHost.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --verbosity quiet (0 warnings/errors). Browser command: SWIFTAID_PLAYWRIGHT_PACKAGE=.codex/case-list-browser/node_modules/playwright absolute path; node tests/SwiftAid.Ui.Tests/ellai-case-tabs.browser.cjs (exit 0). Synthetic Chromium fixture only; no live authorization, persistence, tab mutation, provider or Android evidence. Test-owned host/browser closed; retained task-local runner reused. SW-05-003 remains partial.

SW-05-004 state separation: domain stores Classification, Laboratory and Outcome independently from Workflow. Authorized summary maps classification/workflow verbatim and uses Not assessed priority; no clinical priority inferred. Shared overview now explicitly labels Clinical classification, Administrative workflow and Operational priority. PASS 25/25: dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~CaseSummaryStateSeparationTests --verbosity quiet. PASS 20/20: dotnet test tests/SwiftAid.Domain.Tests/SwiftAid.Domain.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~CaseAggregateTests --verbosity quiet. UI host build passed zero warnings/errors; node tests/SwiftAid.Ui.Tests/ellai-case-tabs.browser.cjs (task-local SWIFTAID_PLAYWRIGHT_PACKAGE) passed at 1280/390px including new explicit labels and all seven tabs. Laboratory/outcome/signal fields are absent from CaseSummaryData and presentation summary; these and live persistence/authorization/Android acceptance remain incomplete. SA-DAT-07/SA-LIF-02/SA-LIF-05 scope; SW-05-004 remains partial.
