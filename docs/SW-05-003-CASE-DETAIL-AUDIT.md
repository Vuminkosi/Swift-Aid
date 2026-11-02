# SW-05-003 case detail surface audit - 2 November 2026

## Outcome

Partial. The shared operations case detail component declares all seven requested tabs: Overview, History, Follow-ups, Assistance, Discussion, Attachments, and Observations. Gateway case detail supplies report, overview, correction, discussion, evidence, observation, timeline and work-management callbacks. The view also includes case coordination work/quick actions. A dedicated escalation/action panel is not established by this source review; some escalation work is represented through separate work-management surfaces.

## Evidence reviewed

- `src/SwiftAid.UI/Components/Surfaces/Operations/CaseDetail/OperationsCaseDetailSurface.razor`: MudTabs for all seven tabs, tab-specific state panels, forms and action handlers.
- `src/SwiftAid.Gateway/Components/Pages/CaseDetail.razor`: authenticated case route and integration callbacks for report/overview/correction/discussion/evidence/observation/work; parameter changes load panels and requests use sequence guards.
- `src/SwiftAid.Mobile/Components/Pages/CaseDetail.razor`: Mobile route reuses the shared case detail surface with Mobile client services.
- `src/SwiftAid.UI/Components/Surfaces/Operations/CaseDetail/CaseDetailPresentationModels.cs` and `CaseDetailContentModels.cs`: presentation contracts for panels.

## Verification

Source inspection only; no new tests or browser/device checks run. The UI component suite result from earlier baseline is not evidence that each tab or action renders and works end to end.

## Not verified / gaps

- Live authorized loading, mutations, scope denial and data freshness for each tab.
- Complete escalation and applicable action panels in the case detail journey.
- Rendered mobile layout, accessibility/keyboard interaction, and browser/Android end-to-end behavior.
- Exact route/client parity and deep-link restoration across all tabs.

## Conclusion

Keep SW-05-003 unchecked and PARTIAL pending live authorized action coverage and rendered browser/device review.

SW-05-003 rendered tab verification: expanded existing EllaiCaseTabsFixture and ellai-case-tabs.browser.cjs to cover Overview, History, Follow-ups, Assistance, Discussion, Attachments and Observations at 1280/390px. PASS route selection, active semantic observations, private-value exclusion, reload restoration, keyboard Enter activation and no horizontal overflow. Initial failures were harness timing (same-tab navigation and post-reload prerender); explicit URL/readiness waits resolved them. Build command: dotnet build tests/SwiftAid.Ui.TestHost/SwiftAid.Ui.TestHost.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --verbosity quiet (0 warnings/errors). Browser command: SWIFTAID_PLAYWRIGHT_PACKAGE=.codex/case-list-browser/node_modules/playwright absolute path; node tests/SwiftAid.Ui.Tests/ellai-case-tabs.browser.cjs (exit 0). Synthetic Chromium fixture only; no live authorization, persistence, tab mutation, provider or Android evidence. Test-owned host/browser closed; retained task-local runner reused. SW-05-003 remains partial.
