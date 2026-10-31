# SwiftAid product recovery progress

**Last reconciled:** 2026-10-31, including the bounded case-correction implementation and its current-tree checks. Status records evidence, not estimated completion. Existing broad working-tree edits are uncommitted and are not accepted deliverables. See [`product-recovery-audit.md`](product-recovery-audit.md), [`UI-ACCEPTANCE.md`](UI-ACCEPTANCE.md), [`UI-SURFACE-MATRIX.md`](UI-SURFACE-MATRIX.md), and [`UI-BACKEND-HANDOFF.md`](UI-BACKEND-HANDOFF.md).

## Current continuation checkpoint (2026-10-31)

| Domain | Journey | Existing implementation | Defects / required changes | Status | Tests and verification | Visual verification | Commit |
|---|---|---|---|---|---|---|---|
| Web public entry | `/` → report, reference library, Community, sign-in | `BrowserGuestRoot`, shared public website shell/landing; Gateway public layout | Website entry lacked a SwiftAid-specific explanation and inherited the app side navigation; public header included Education. Replaced Home composition with a website shell and clear report-first service description; header exposes Community and sign-in. Route-focus outline on `h1` removed while preserving focus navigation. | Implemented; runtime verified | Gateway Debug build PASS, 0 warnings/errors; Docker Gateway Release rebuild and single-service restart PASS; Gateway healthy; Home, sign-in, registration, report, Community, education return 200; protected Ellai route redirects to sign-in. Cross-root Community navigation and page content observed in browser. | Desktop Home capture and accessibility tree confirm brand, Community/sign-in header, no side menu, full landing sections. Narrow viewport/theme comparison NOT VERIFIED. | Not committed; existing staged/unstaged work overlaps touched files. |
| Web/Mobile onboarding | `/register` → account creation → welcome completion | Gateway and Mobile forms use real capability/password-policy APIs; Gateway registration uses the existing registrar and profile service | Added required Full name on Web and Mobile. The request now carries it to Gateway; `PasswordIdentityRegistrar` persists it to `user_profiles` in the same transaction as actor/access state/Identity user, and Gateway verifies the stored profile before signing in. Both forms use DataAnnotations validation and accessible submission feedback. | Implemented slice; PARTIAL overall | Gateway Debug build PASS with `-p:WasmBuildNative=false` (0 warnings/errors); Mobile Android Debug build PASS (0/0). Focused `PasswordRegistration_RequiresValidDisplayNameBeforeCallingRegistrar` PASS 4/4; `IdentityEndpointSecurityTests` PASS 14, 10 PostgreSQL-gated skips. Docker Release image rebuilt and Gateway force-recreated; container healthy, `/health/live`, `/register`, `/sign-in` return 200. Successful account creation, live DB profile roundtrip, duplicate handling, verification, and membership landing NOT VERIFIED. | Live `/register` accessibility tree confirms Full name, Email, Password, and server password policy. Visual sizing/error states at desktop/narrow widths and Android device rendering NOT VERIFIED. | Not committed; existing staged/unstaged work overlaps touched files. |
| Password recovery | `/forgot-password` → accepted reset request → `/reset-password` | Existing antiforgery-protected Gateway reset endpoints and generic enumeration-safe response; shared Web/Mobile confirmation surface | Web and Mobile request/reset pages call real Gateway endpoints. Mobile retrieves the actual password policy and validates confirmation before posting. On HTTP-accepted requests both clients switch to a shared “Check your inbox” state with a real resend action and neutral account-existence copy. | Partial | Gateway Debug build PASS, 0 warnings/errors; Android Debug build PASS, 0 warnings/errors; focused reset-surface UI test PASS; Docker Gateway Release rebuild/restart PASS and healthy, `/health/live` 200, served CSS includes the shared surface. | Web forgot-password request form visually checked at 1280×900, 390×844, and 320×640; device interaction, accepted-state visual check, mailbox delivery, and valid reset-token completion NOT VERIFIED. | Not committed; existing staged/unstaged work overlaps touched files. |

## Ellai Gateway entry checkpoint (2026-10-31)

The missing authenticated Gateway launcher entry is now connected: shared `AuthorizedLayout` and authenticated `PublicLayout` render `EllaiLauncherMenu`, forward its selected mode through the scoped `EllaiLauncher`, and retain the single `EllaiGuidanceHost` subscriber. The rendered-layout regression test invokes the actual menu callback and asserts the selected mode reaches the launcher event. Full UI suite PASS 206/206; the Gateway image containing the layout and SwiftAid brand-mark updates is deployed and healthy. This does not verify a live authorized Ellai session, approval, or browser appearance. MAUI still has no Ellai host/client registration, and remote mobile service contracts plus device verification remain open.

| Domain / milestone | Journey and source | Confirmed gaps / dependency | Status | Verification evidence | Commit |
|---|---|---|---|---|---|
| Foundation (M1) | Gateway and Mobile shells/navigation; shared `src/SwiftAid.UI/Components/Layout`; dashboard/navigation links | PublicLayout fix is committed as `af237a9c0d8a9ba5d9b188c3770ee320acd19017`; 320/360/390/430px and 1280px behavior is browser-checked. Disabled-Identity challenge fix is committed as `dfdfa27bee6e7262608a86edbf114066cddff594`; post-commit candidate verifies `/dashboard` → `/sign-in?returnUrl=%2Fdashboard`. Guest Home Staff workspace entry is committed as `11821e57602adf9435917e5519243b75c922cd87`. Authorized shell/notifications implementation is committed as `f1e54a34925aadbe84dfba9a1883d27c19002f77`. Guest More Escape/focus handling is committed as `9e15ba9cd48df40dc8a6b5333a94d6eece2f73af`. Explicit More `aria-expanded` string correction is committed as `32eb61ba926564c01436880b22d839b975e310cf`. At 390x844 in isolated guest candidate, More reports false/true as closed/open; Escape from trigger and Education link closes it and restores focus. M1 remains partial: identity/provider/database, authenticated notification menu/focus, screen reader, other viewport/theme design parity, and device journeys are NOT VERIFIED. | In progress; NOT VERIFIED | Current isolated disabled-auth/no-DB candidate: Home workspace link navigates to `/sign-in?returnUrl=%2Fdashboard`; guest report Type→Details→save truthfully reports local-only save; tracking shows one synthetic draft after reload. Draft was not submitted and has no narrative/patient data. Docker image build PASS with Browser `e_sqlite3.a` native-link warning; Gateway build PASS (0 warnings/errors); Android Debug build PASS (0/0); full UI suite PASS 186/186. `adb devices -l` found no devices. | Guest-save, disabled-Identity challenge, Staff workspace-entry, authorized-shell, More Escape/focus, and More aria-state slices committed. |
| Authentication/onboarding (M2) | Gateway/Mobile welcome, registration, sign-in, verification and account pages; `designs/onboarding/requirements.md` and `designs/onboarding/01…14` | Role-aware completion, federation/provider readiness, persistence, staff login and error recovery are not proven end-to-end. Role selection cannot grant authority. | Partial / NOT VERIFIED | Historical source/capture evidence in UI acceptance; Google/Facebook and full journey not verified. | Not established for current working-tree changes. |
| Guest report (M2) | `/` → `/report` → type/details/location/optional voice/review → local/remote submission → receipt; `GuestReport`, `GuidedReportStepper`, intake store/client | Local browser draft save reports success only after storage confirms it; the Details step stays active, and local history survives reload. Full validation, failed/unavailable storage, Android restart/recovery, connectivity recovery, voice/device, real submission/receipt/routing, and responsive/accessibility checks remain open. No guest-media upload contract. | Partial; critical journey open | 2026-10-30 browser flow and focused tests PASS; no report text or patient data entered and no submit. | Save slice committed as `74f30d86b452c138028119717d86a7c820bae2ac` (four paths). |
| Case management (M3) | `/my-cases` → `/case/{id}` → timeline/commands; shared case queue/detail/timeline/command components | Full role-authorized lifecycle, discussion/audio, assistance/follow-up/escalation linkage, deep link/back context and persistence need live-contract journey checks. The bounded legacy-report correction form is implemented in the current uncommitted Gateway/Mobile tree; full D13 prior-revision/source comparison remains unavailable. | Partial / NOT VERIFIED | Correction query/endpoint commits `bc037d7` and `5c9edb2`; client/UI changes uncommitted. Focused UI tests PASS 12/12, full UI suite PASS 201/201, Android Debug build PASS (0 warnings/errors), scoped Integration filter PASS 8/8. Browser/device, authorized persistence/conflict, refreshed-success, accessibility/theme and complete case journeys remain NOT VERIFIED. | Query/endpoint committed; current adapters and UI remain uncommitted pending review and real-environment verification. |
| Surveillance (M4) | `/surveillance`, map/aggregate surfaces, filters and case navigation | No live map aggregate adapter evidenced; inference is disabled. Verify filters, empty/fail-closed states and privacy/permission boundaries with an actual authorized source. | Source partial; backend-dependent / NOT VERIFIED | UI surface matrix / backend handoff. | Not established. |
| Community sessions (M5; outside Recovery Batch 1) | `/sessions` browse → detail/editor → authorized schedule/cancel/complete; participant/material/follow-up/feedback screens from `designs/community-sessions` | Staff session endpoints fail closed; in-process staff reads/writes are denied while public session routes remain available. Mapper validation tests for all three mappers are present; focused Integration tests now pass. S1 UX gaps remain for participants, materials, follow-ups, and feedback; required backend contracts/capabilities remain missing. Public subscription consent/options, attendance and social delivery are unavailable; cancellation reason contract needs resolution. | Partial; S1 gaps remain / NOT VERIFIED | `CommunitySessionEndpointTests`: PASS, 37/37 with `WasmBuildNative=false`. Community sign-in panel refresh fix: UI + Infrastructure build PASS. Fieldset/legend and distinct remove labels are source-fixed; screen-reader/browser/device validation not run. Backend handoff documents missing contracts. | Current changes uncommitted; no milestone commit. |
| Education (M5) | `/education`, search/filter/offline resource actions | Live approved catalog/download adapter not evidenced. No generated medical/veterinary advice. | UI source; production capability unavailable / NOT VERIFIED | UI surface matrix / backend handoff. | Current edits uncommitted. |
| Analytics/reporting (M6) | `/dashboard`, `/reports`, filters/drilldowns/export | No live aggregate/export endpoint or data freshness source evidenced; fake development data cannot stand in for production. | UI source; production capability unavailable / NOT VERIFIED | UI surface matrix / backend handoff. | Current edits uncommitted. |
| Organizations/admin (M6) | `/organizations`, profile/settings, permission-controlled actions | No live org/membership endpoints or concurrency semantics evidenced. | UI source; backend contract missing / NOT VERIFIED | UI surface matrix / backend handoff. | Current edits uncommitted. |
| Ellai integration (M7) | `/ellai/about`; shared composer/approval components and layouts under `src/SwiftAid.UI/Ellai` | Authenticated Gateway layouts render the launcher and host. Current-scope lookup is used when available; an identity-only empty-grant scope permits account session entry when workspace scope lookup is unavailable. This grants no workspace/case tools. Free text remains `UnknownOrigin` and is blocked before provider dispatch. Guest WebAssembly still has no host/session identity, Mobile has no launcher/host, and operation-specific approval/review authorization is not wired. | PARTIAL / end-to-end journey unavailable | Gateway build PASS, 0 warnings/errors; Integration `EllaiPrivilegedRouteAuthorizationTests` PASS 12/12 (active scope, identity-only fallback, and privileged-denial cases); `AuthorizedLayoutNavigationAccessibilityTests` PASS 5/5. Docker Gateway Release rebuild/restart PASS; container healthy and `/health/live` HTTP 200. Served UI CSS contains the navbar toggle. Authenticated browser/provider and Mobile/device journeys NOT VERIFIED. | Not committed; existing staged/unstaged work overlaps touched paths. |
| Notifications/profile/sync (M7) | `/notifications`, `/account`, `/profile`, `/sync`; notification center/feed | Delivery, account switching, authorization changes, retry/restart/outbox and complete recovery require host-specific verification. | Partial / NOT VERIFIED | UI matrices; no new runtime checks. | Not established for current changes. |
| Stabilization (M8) | End-to-end web + Android, all approved journeys and responsive/theme/accessibility checks | Full report submission, mobile device layouts, themes, screen-reader and supplied-design parity checks remain required. PublicLayout and guest More keyboard navigation have isolated browser evidence but do not establish full app/device parity. | NOT VERIFIED | 2026-10-30: full UI suite 186/186; Gateway and Android Debug builds 0 warnings/errors. Candidate Docker image build passed with Browser SQLite native-link warning. Candidate 18082 has disabled auth/no DB; guest report saved one local synthetic draft and it persisted after reload, without submission or narrative/patient data. `adb devices -l` found no devices. Identity/provider/database, authenticated notification focus, screen reader, broader viewport/theme/design parity, and device journeys remain NOT VERIFIED. | No recovery milestone commit established. |

## Current-tree Ellai and verification update

- **Latest checks (2026-10-30):** `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false -v:minimal` PASS (484 passed, 3 skipped); `dotnet test tests/SwiftAid.Client.Tests/SwiftAid.Client.Tests.csproj --no-restore -m:1 -nr:false -v:minimal` PASS (242 passed). `dotnet build src/SwiftAid.UI/SwiftAid.UI.csproj --no-restore -m:1 -nr:false -v:minimal`, `dotnet build src/SwiftAid.Infrastructure/SwiftAid.Infrastructure.csproj --no-restore -m:1 -nr:false -v:minimal`, and `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -c Debug -f net10.0-android --no-restore -m:1 -nr:false -v:minimal` PASS (each 0 warnings/errors). Full UI suite PASS 168/168 using `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false`; focused IdentityEndpointSecurityTests PASS 14 with 10 gated skips; CommunitySessionEndpointTests PASS 37; Ellai surface focused PASS 28/28; cancel-conflict focused PASS 1/1. Focused Integration runs also pass using `WasmBuildNative=false`; default test invocation is blocked before test compilation when `emcc` is missing. No DB/device/screen-reader or full design-comparison checks ran.
- **Gateway candidate/live-state checkpoint (2026-10-30):** `infra/compose.yml` defaults `Federation__Enabled` and `Identity__Enabled` to false. A candidate image built from the then-current Dockerfile failed startup: minimal API inference rejected Identity handlers mapped without Identity DI services. Current source now conditionally maps those handlers and adds `/auth/capabilities`; static review PASS. An earlier candidate runtime and desktop smoke check passed as noted below. Focused IdentityEndpointSecurityTests pass with `WasmBuildNative=false` (14 passed, 10 gated skips); default native-WASM execution requires missing `emcc`. Separately, the Gateway already live at `127.0.0.1:8080` is an older/different UI bundle: anonymous pages expose the Ellai launcher/menu, which is absent in current source. Do not claim source and deployed image match. The existing container was left untouched. No host environment or secrets are documented here.
- **Candidate runtime and desktop browser (2026-10-30):** an earlier rebuilt image from current source started in isolation with Identity/Federation disabled. `/health/live`, `/`, `/sign-in`, `/register`, and `/auth/providers` returned 200; `/auth/capabilities` returned `false`; `/auth/antiforgery` returned 404; `/auth/password/policy` returned 503. IAB desktop rendered sign-in and registration unavailable states; guest flow reached report type and then details after selecting Human health. No text was entered or submitted. This smoke check predates the final scoped-CSS selector correction. The final CSS correction anchors generated MudBlazor descendant and responsive-navigation rules under `.swiftaid-public-header`; UI build PASS with 0 warnings/errors. Candidate image build PASS with a Browser SQLite native linking warning. An isolated candidate subsequently started with a compose-equivalent ephemeral tmpfs key ring (no database connection); at `/sign-in`, widths 320/360/390/430 had no header overflow, toggle and sign-in controls were 44px tall, and mobile navigation was initially hidden. At 390px, opening exposed Education/Community and set expanded state; Escape from the toggle and Education link closed/hidden the menu, set `aria-expanded=false`, and restored toggle focus. Desktop 1280px showed desktop links and hidden mobile toggle/menu. This verifies PublicLayout browser navigation only. Screen-reader, mobile device, themes and full report submission remain unverified. The pre-existing Gateway at `127.0.0.1:8080` was left untouched and remains an older/different UI bundle than current source; source/image parity is not confirmed.
- **Dashboard/navigation:** dashboard GUID case links are implemented and independently source-reviewed PASS; rendered Gateway/browser behavior and the UI test are NOT VERIFIED. Protected-route return now preserves local route/query/hash; the full UI suite passes 168/168 with `WasmBuildNative=false`, including its UI coverage.
- **Community (M5, outside Recovery Batch 1):** staff session endpoints fail closed and in-process reads/writes are denied; public session routes remain. Mapper validation tests for all three mappers are present; CommunitySessionEndpointTests PASS 37/37 with `WasmBuildNative=false`. Community sign-in panel refresh fix built successfully. Session editor fieldset/legend and unique remove labels are source-fixed; screen-reader behavior is NOT VERIFIED. UX S1 gaps remain in participants/materials/follow-up/feedback, and backend contracts/capabilities are missing. Do not fabricate these workflows. Journeys B/C/D remain deferred from Recovery Batch 1; community is a later milestone.
- **Accessibility and design:** fieldset/legend and unique remove-label issues are corrected in source only. No screen-reader/browser/device check or full design-reference comparison ran; parity remains PARTIAL.

- **Authorization/UI entry:** `AuthorizedLayout` and authenticated `PublicLayout` render the Ellai launcher/host. Account session start preserves current grants where the scope source is available and otherwise creates an identity-bound zero-grant help session; this fallback grants no workspace/case tools. Guest WebAssembly and Mobile still have no Ellai host/session identity. Privileged approval/review remains fail-closed without operation-specific grants.
- **Utterance processing:** arbitrary free text remains `UnknownOrigin` and is blocked. A separate typed public-product-help path classifies locally to approved product topics, requires fixed purpose-specific consent, and persists only a sanitized prompt with the `ellai_productfacts` tool allowlist. Guest users now receive an empty-scope, short-lived cookie-bound Ask session through a separate rate-limited/antiforgery-protected API; guest questions share the same classifier and durable outbox and cannot access staff, case, receipt, approval, or generated-navigation tools. Source and TestServer evidence exists; live browser cookie/provider behavior is **NOT VERIFIED**.
- **Interaction recovery:** session-start/cancel/submit reconciliation and approval reject/cancel changes are present. Ellai UI/source reviews found no actionable findings; runtime UX/accessibility acceptance remains NOT VERIFIED. Do not call those journeys verified.
- **Ellai rejection/cancellation evidence:** rejection handling now distinguishes a confirmed rejection from a false result or exception with unknown outcome; tests are covered by the full UI suite, PASS 168/168 with `WasmBuildNative=false`. Completed-session cancellation/parity passed a focused Application check (7/7); the corresponding database-specific integration verification remains unverified. This does not establish a complete Ellai journey.

### Ellai public-help outbox checkpoint (2026-10-31)

The authenticated and guest typed-question paths now ask for explicit purpose-specific consent before sending text, reject unknown or sensitive content through the local public-help classifier, store only the reduced topic prompt in the accepted session/outbox transaction, and run it through the durable PostgreSQL outbox. Guest sessions use a dedicated signed cookie identity, an empty workspace scope, antiforgery validation, and per-IP rate limits; the WebAssembly public root shows Ask only. The worker enforces session ownership and epoch fences, ordered single-flight claims per session, lease recovery, consent/usage hydration, bounded retries and payload clearing. `EllaiUtteranceSubmissionServiceTests` + `EllaiUtteranceOutboxTests`: PASS 13/13. Ellai UI tests: PASS 68/68 with `BuildProjectReferences=false`. Ellai route authorization tests: PASS 13/13; public-help consent/outbox integration: PASS 1/1; guest cookie/consent/utterance integration: PASS 1/1; all with `BuildProjectReferences=false`. Gateway source build PASS (0 warnings/errors) with prebuilt references. Browser WASM managed build PASS (0 warnings/errors) with `WasmBuildNative=false`; default native linking is unavailable because `emcc` is not installed. Docker/PostgreSQL migration application, actual browser cookie behavior, live provider dispatch, voice round-trip, and mobile device behavior remain **NOT VERIFIED**; Docker remains unavailable because `D:` is full and its service cannot be started from this session.
- **Cycle limit:** current Application default and PostgreSQL constraint both use 12 cycles. The dedicated PostgreSQL cycle-limit regression test is added but not executed in this checkpoint. Earlier DB tests are historical and do not replace a current DB run.
- **Reported Ellai checks:** the latest `Application.Tests` total is 484 passed / 3 gated skips (supersedes the earlier 208 Ellai-filtered result); `Client.Tests` 242 passed. UI, Infrastructure, and Mobile Debug Android builds passed with 0 warnings/errors. Full UI and focused Identity/Community Integration runs pass with `WasmBuildNative=false`; default WASM-native invocation is blocked before test compilation because `emcc` is missing. No current database, browser, device, or screen-reader run was executed. `EllaiEndpointMetadataTests`, `EllaiPrivilegedRouteAuthorizationTests`, `EllaiSessionCancellationEffectsTests`, and `EllaiPostgresCycleLimitTests` are present but unexecuted. Current cycle default/check constraint is 12; the earlier default-100 mismatch is obsolete.
- **Design parity:** still PARTIAL. Do not promote a source build or these focused tests into parity, accessibility, or complete user-journey acceptance.

## Guest draft and staff workspace entry checkpoint (2026-10-30)

The isolated candidate Gateway was built from the current source, run at `127.0.0.1:18080` without a database connection, and configured with Identity/Federation disabled and an ephemeral tmpfs data-protection key ring. The live Gateway at `127.0.0.1:8080` was not modified. Docker publish passed with the warning that Browser SQLite native `e_sqlite3.a` was not linked.

The candidate browser exercised a synthetic guest report: selected Human health, advanced to Details, and saved. The first live attempt revealed that successful save returned the form to Type. After remediation and a fresh rebuild, saving retained Details, displayed “Your report draft is saved on this device. It has not been sent,” and placed the draft in `/report?view=tracking`; the item remained after reloading the history page. No report text, patient data, or server submission was used. This does not verify unavailable-store failure, server submission/receipt, or Android behavior.

The guest Home rendered “Staff workspace” → `/dashboard`. The first disabled Identity/Federation candidate returned HTTP 500 because no default challenge scheme was registered. The challenge fix is committed as `dfdfa27bee6e7262608a86edbf114066cddff594`; independent code and security reviews passed. Post-commit candidate checks passed for `/dashboard` → `/sign-in?returnUrl=%2Fdashboard`, live health 200, and anonymous notifications 401. `git show --check --oneline HEAD` passed. The image was built from the same source before selective commit; no push occurred. Protected-route authorization was not bypassed. Enabled Identity/provider mode and the full logged-in journey remain NOT VERIFIED. The “Staff workspace” entry is committed as `11821e57602adf9435917e5519243b75c922cd87`; More Escape/focus handling is committed as `9e15ba9cd48df40dc8a6b5333a94d6eece2f73af` in `BrowserGuestRoot.razor`, with precise staging excluding Ellai removals. Browser/mobile keyboard behavior remains NOT VERIFIED. The authorized shell is committed and not accepted for live authenticated behavior. The combined `GuidedReportDraftPersistenceTests` and `AuthorizedLayoutNavigationAccessibilityTests` run passed 6/6 with `WasmBuildNative=false`. Drawer ARIA semantics have a static independent code review and rendered component test; live authenticated-shell and screen-reader checks remain NOT VERIFIED. Login/provider readiness, real intake routing, themes, mobile device, full design parity, and complete user journeys remain open.

## Guest-save commit and workspace-return follow-up (2026-10-30)

The guest-save implementation was committed on unchanged `master` as `74f30d86b452c138028119717d86a7c820bae2ac`. Its four paths are `src/SwiftAid.Browser/Components/BrowserGuestRoot.razor`, `src/SwiftAid.UI/Components/Public/GuidedReportStepper/GuidedReportStepper.razor`, `src/SwiftAid.UI/Components/Surfaces/Public/PublicReportSurface.razor`, and `tests/SwiftAid.Ui.Tests/GuidedReportDraftPersistenceTests.cs`. The commit covers truthful save outcomes and draft-step retention; it does not include the later uncommitted StaffWorkspace/Escape changes in BrowserGuestRoot.

The disabled-Identity route challenge passes in the isolated live candidate and is committed as `dfdfa27bee6e7262608a86edbf114066cddff594`. Independent code and security reviews passed. Post-commit checks returned live health 200, anonymous notifications 401, and protected dashboard 302 to `/sign-in?returnUrl=%2Fdashboard`; `git show --check --oneline HEAD` passed. The image was built from the same source before the selective commit; no push. Protected-route authorization remains enforced. Enabled Identity/provider mode and full logged-in journey remain NOT VERIFIED. The “Staff workspace” entry is committed as `11821e57602adf9435917e5519243b75c922cd87`; More Escape/focus handling is committed as `9e15ba9cd48df40dc8a6b5333a94d6eece2f73af`. Precise staging excluded Ellai removals. Browser/mobile keyboard behavior remains NOT VERIFIED. The authorized shell is committed but live authenticated behavior is not accepted. Other end-to-end, mobile, accessibility, and design-parity gates remain open.

The auth commit contains exactly `src/SwiftAid.Gateway/Program.cs` and `tests/SwiftAid.Integration.Tests/DisabledIdentityChallengeTests.cs`. The separate Ellai service-registration change is unstaged and was not part of this commit.

## Guest Staff workspace entry and shell-review follow-up (2026-10-30)

The guest Home Staff workspace entry was committed as `11821e57602adf9435917e5519243b75c922cd87`. Exact scope: `src/SwiftAid.Browser/Components/BrowserGuestRoot.razor`, `src/SwiftAid.UI/Components/Surfaces/Public/PublicHomeSurface.razor`, and `tests/SwiftAid.Ui.Tests/PublicHomeStaffWorkspaceEntryTests.cs`. Focused tests passed 3/3; the combined focused run passed 12/12; the Gateway build passed with zero warnings/errors. The disabled-Identity route behavior remains verified in the isolated candidate: `/dashboard` redirects to `/sign-in?returnUrl=%2Fdashboard`. Guest More Escape/focus handling is committed separately as `9e15ba9cd48df40dc8a6b5333a94d6eece2f73af` in `BrowserGuestRoot.razor`; precise staging excluded Ellai removals. Browser/mobile keyboard behavior remains NOT VERIFIED.

The initial UX review found that an invalid notification action could mark the notification as read without navigating, and the accessibility review requested focus management after dismissal. The committed implementation now hides invalid Open actions with status text and provides a dismissal focus fallback, confirmed by later source review and rendered component coverage. The custom renderer cannot exercise the focus API calls, so live keyboard focus and authenticated notification interactions remain NOT VERIFIED; the authorized shell is not accepted.

## Authorized shell and notification commit checkpoint (2026-10-30)

The authorized workspace shell, notification menu/feed, and sign-in return handling were committed as `f1e54a34925aadbe84dfba9a1883d27c19002f77`. Exact 13-path scope: `src/SwiftAid.Gateway/Components/Layout/AuthorizedWorkspaceLayout.razor`, `src/SwiftAid.Gateway/Components/Layout/NotificationMenu.razor`, `src/SwiftAid.Gateway/Components/Layout/NotificationMenu.razor.css`, `src/SwiftAid.Gateway/Components/RedirectToSignIn.razor`, `src/SwiftAid.Gateway/Components/Routes.razor`, `src/SwiftAid.UI/Components/Layout/AuthorizedLayout.razor`, `src/SwiftAid.UI/Components/Layout/AuthorizedLayout.razor.css`, `src/SwiftAid.UI/Components/Layout/StartupLayout.razor`, `src/SwiftAid.UI/Components/Layout/SwiftAidNavMenu.razor`, `src/SwiftAid.UI/Components/Notifications/NotificationFeed.razor`, `tests/SwiftAid.Ui.Tests/AuthorizedLayoutNavigationAccessibilityTests.cs`, `tests/SwiftAid.Ui.Tests/NotificationMenuRenderedTests.cs`, and `tests/SwiftAid.Ui.Tests/RedirectToSignInTests.cs`.

Post-commit verification: full `SwiftAid.Ui.Tests` passed 186/186; Gateway build passed with zero warnings/errors; `git show --check --oneline HEAD` passed. Independent code review found no blocking findings (PASS); architecture review found no findings (PASS); security review found no actionable issue in scope (PASS). UX review identified an S2 invalid notification action that could mark read without navigating; later source review found invalid Open actions hidden with explanatory status and a dismissal focus fallback. Accessibility review found drawer ARIA state semantics covered but recommends NOT VERIFIED for live keyboard/screen-reader acceptance. Focus API calls could not be exercised by the custom test renderer. Authenticated browser interactions, keyboard focus, screen-reader behavior, enabled provider configuration, live notification mutation, device UI, and design parity remain NOT VERIFIED.

The guest More Escape/focus handling slice was committed as `9e15ba9cd48df40dc8a6b5333a94d6eece2f73af` in `src/SwiftAid.Browser/Components/BrowserGuestRoot.razor`; precise staging excluded Ellai removals. Live browser/mobile keyboard behavior remains NOT VERIFIED. Overall M1 and Stabilization remain PARTIAL / NOT VERIFIED; source commits and test success do not complete logged-in journeys, backend integration, mobile/device acceptance, or comparison against the supplied design references.

## More aria-state and isolated candidate checkpoint (2026-10-30)

The explicit More `aria-expanded` string fix is committed as `32eb61ba926564c01436880b22d839b975e310cf`. In the isolated guest candidate at port 18082 (Identity/provider authentication disabled; no database), browser checks at 390x844 observed More `aria-expanded=false` while closed and `true` while open. Escape closed the menu from both the trigger and the Education link and restored focus to the trigger. The Home Staff workspace link navigated to `/sign-in?returnUrl=%2Fdashboard`.

In the same candidate, a synthetic guest report moved Type → Details → Save; the UI truthfully reported local-only storage and not-sent status. Tracking showed one synthetic draft after reload. It was not submitted and contains no narrative or patient data. The candidate containers and images have been stopped and removed. The synthetic draft remains in the local browser profile's origin storage for `127.0.0.1:18082`; the UI provided no delete control. Docker image build passed with the Browser `e_sqlite3.a` native-link warning. Gateway build had 0 warnings/errors, Android Debug build had 0 warnings/errors, and the full UI suite passed 186/186. `adb devices -l` found no devices.

Identity/provider/database behavior, authenticated notification menu and focus, screen-reader behavior, other viewport/theme design parity, and mobile device behavior remain NOT VERIFIED. M1 and Stabilization remain PARTIAL / NOT VERIFIED.

## Current acceptance snapshot

- **UI-01 desktop surfaces:** PARTIAL / NOT VERIFIED; full UI tests pass with `WasmBuildNative=false`; PublicLayout navigation is post-commit verified in an isolated browser at 320/360/390/430px and desktop 1280px; full desktop surfaces and app journeys remain unverified.
- **UI-02 mobile surfaces:** PARTIAL / NOT VERIFIED.
- **UI-03 shared design, responsive and accessibility:** PARTIAL; design parity gate open.
- **UI-04 real contracts and state coverage:** PARTIAL.
- **UI-05 Journeys A–H:** NOT VERIFIED end-to-end; Journey A has partial historical entry/step evidence. Ellai is currently fail-closed and has no rendered launcher entry point.
- **UI-06 privacy/safety boundaries:** contracted, with source checks required for each changed view.
- **UI-07 handoff documentation:** present in working tree; independent review/commit status must be checked.
- **UI-08 reviewed, committed, post-commit result:** NOT VERIFIED.

## Evidence update protocol

For each milestone update, record exact path/route, command/tool, environment, revision, result, screenshots/traces if available, and explicit limits. Keep the labels **PASS**, **FAIL**, **NOT VERIFIED**, and **NOT APPLICABLE** scoped to the check actually run. Don't turn builds, route counts, fake-service output, screenshots, or historical results into end-to-end acceptance. Preserve existing uncommitted work and record real commits only after review and required checks.

## Account sign-out and navigation responsiveness (2026-10-31)

Confirmed web sign-out now force-loads `/` (the public SwiftAid landing page) instead of returning to `/account`. The signed-out Account surface now provides direct sign-in, account registration, and guest-report choices, with the Gateway wiring each to its existing route. In the authorized shell, mobile navigation uses a closed temporary drawer and desktop navigation uses a persistent drawer. CSS media queries switch the drawers at 960px without a viewport subscription or client-side breakpoint wait.

Focused tests passed 9/9 for authorized navigation structure and expanded-state behavior, guest account actions, and confirmed sign-out navigation. The complete `SwiftAid.Ui.Tests` project passed 205/205 with `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false`. Docker Gateway Release image build passed and `docker compose -f infra/compose.yml up -d --no-deps gateway` recreated only Gateway. The running Gateway reported healthy; live `GET /`, `/account`, `/sign-in`, `/register`, and `/report` returned HTTP 200. Live browser inspection confirmed the signed-out account choices render with the intended routes.

The Docker publish emitted the existing Browser `NativeFileReference` warning about `e_sqlite3.a` not being linked for Wasm; Gateway publish completed. Authenticated browser sign-out, real-provider login, keyboard/screen-reader behavior, mobile-device acceptance, and full design parity remain **NOT VERIFIED**. ActualLab.Fusion was not introduced because these changes concern local account routing and shell responsiveness; no existing UI Fusion dependency was found. MudBlazor 9.11's installed documentation confirms MudHidden listens for viewport changes through JavaScript, so the shell uses CSS breakpoints for the immediate first-frame layout.

### Account overview and identity display follow-up (2026-10-31)

The live `/account` guest view was reorganized after browser inspection showed repeated guest labels, unavailable provider rows, an unauthorized workspace retry warning, and a disabled theme demo card. The signed-out view now presents guest status and its three existing next steps in one divided panel, shows workspaces as sign-in-gated, and hides authenticated-only provider settings. Account preference sections now render as separated cards; the example preview card was removed. Signed-in display identity now resolves name, username, then email claims so an authenticated account without a name claim still identifies the account owner.

Focused account/navigation tests passed 11/11; full `SwiftAid.Ui.Tests` passed 206/206. The final Docker Gateway Release image was built and restarted using `docker compose -f infra/compose.yml build gateway` and `docker compose -f infra/compose.yml up -d --no-deps gateway`. Gateway became healthy, and `/`, `/account`, `/sign-in`, `/register`, `/report` returned HTTP 200. The final signed-out Account route was visually inspected in the in-app browser at its current desktop viewport; the guest panel, existing destinations, sign-in-gated workspace row, and separated preference cards rendered. The name/email fallback is covered by a signed-in principal test; actual authenticated browser rendering and real provider claims remain **NOT VERIFIED**. Mobile viewport, screen reader, and full design parity remain **NOT VERIFIED**.

## Dashboard handoff, responsive navigation, and reconnect image follow-up (2026-10-31)

The default successful web sign-in destination is `/dashboard`; Account and workspace sign-in entry points use the same destination, while explicit protected deep links keep their requested return path. Authenticated requests to `/` now use the server-rendered authorized app instead of the standalone anonymous Browser shell. The home route and public header use `AuthorizeView` so they follow the current auth state. Authorized web, Android, and guest navigation no longer list Education; the Education page remains available by route and through the landing-page reference link. Android `/dashboard` aliases its signed-in overview, and mobile sign-in defaults there. The reconnect modal's JavaScript-created GIF image is now selected through `::deep` CSS and constrained to its 150×150 container with `object-fit: contain`.

The final Docker Release build completed and `docker compose -f infra/compose.yml up -d --no-deps gateway` recreated only Gateway. Gateway health was healthy; `/`, `/sign-in`, `/account`, and `/report` returned 200, while `/dashboard` and `/my-cases` returned 302 to the configured auth-provider route. The served `SwiftAid.UI` bundle contains the responsive drawer rules and has been linked from the Gateway document; the served Gateway bundle contains the reconnect image rule. The signed-out landing page rendered in the in-app browser. The Android Debug build passed with zero warnings and errors. No tests were run for this follow-up. Real-provider sign-in, authenticated home/header transitions, authorized menu interaction at narrow and wide viewports, Android-device behavior, and the reconnect modal's live animation remain **NOT VERIFIED**.


### Reconnect GIF sizing correction (2026-10-31)

The reconnect modal creates its GIF `<img>` dynamically. To ensure sizing applies regardless of Razor scoped-CSS matching, `ReconnectModal.razor.js` now sets explicit 150×150 HTML dimensions and inline pixel bounds on each generated image, with `object-fit: fill`; the 150×150 clipped wrapper remains in `ReconnectModal.razor.css`. The GIF asset canvas is 480×480.

`docker compose -f infra/compose.yml up -d --build --no-deps gateway` **PASS**: Release Gateway image built and only Gateway was recreated. `docker compose -f infra/compose.yml ps gateway` **PASS**: healthy at `127.0.0.1:8080`. `GET /sign-in` returned HTTP 200. Fetching the fingerprinted reconnect module from the served `/account` page **PASS** and confirmed the served script contains 150px inline dimensions and `object-fit:fill`. `git diff --check` **PASS** for the touched reconnect styles/script. The reconnect state was not forced in the live browser, so visual animation and clipping behavior remain **NOT VERIFIED**. No tests were run.

## Ellai false unknown session-start warning (2026-10-31)

The reported warning was produced when the Gateway's `HasCurrentScopedStaffAuthorizationAsync` preflight returned false. `EllaiReviewGatewayClient.StartSessionAsync` represented both that known preflight denial and failures during persistence as `null`; `EllaiGuidanceHost` treated every null/exception as a possibly-created remote session and displayed the support/reconciliation warning. `IEllaiReviewClient.StartSessionAsync` now returns `EllaiSessionStartResult` with an explicit outcome. The UI displays confirmed denial, invalid request, or known unavailability as a visible host status stating that no session was created. The no-duplicate-start guard remains active only when the write/response outcome is genuinely ambiguous. HTTP 401/403, 400, and 422 have distinct outcomes; server errors, cancellation, malformed success responses, and transport exceptions remain unknown.

`dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false --filter "FullyQualifiedName~EllaiSurfaceLogicTests|FullyQualifiedName~EllaiEventPollerTests"` **PASS**, 42/42. `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false --filter "FullyQualifiedName~EllaiPrivilegedRouteAuthorizationTests"` **PASS**, 10/10; the direct Gateway preflight test confirmed policy denial without an additional session-store create. Release image publish and `docker compose -f infra/compose.yml up -d --build --no-deps gateway` **PASS**; Gateway is healthy, `/health/live` and `/sign-in` return 200, and `/dashboard` redirects to the configured provider route. Docker publish retains the Browser SQLite native-link warning.

Current limitation: the host's trusted staff/scope preflight deliberately remains fail-closed, so this correction does not enable Ellai. Real authenticated web/provider interaction, Android integration/device behavior, and full Ellai journey remain **NOT VERIFIED**. No credentials or `.env` values were read. The stale launcher statements in `docs/ellai/CAPABILITIES.md` and `IMPLEMENTATION-STATUS.md` were corrected; those files are ignored by the repository's existing `/docs/ellai` ignore rule and were not staged.

### Authorized drawer breakpoint repair (2026-10-31)

| Field | Result |
|---|---|
| Domain and journey | Foundation / authorized Gateway navigation; resize across the MudBlazor Md breakpoint and check drawer/content reflow. |
| Implementation | `AuthorizedLayout.razor` and `.razor.css` render only the persistent desktop drawer or temporary mobile drawer using MudBlazor 9.11 `MudHidden`. |
| Confirmed defect | CSS hid an always-open persistent drawer without removing it from MudLayout state; a second temporary drawer was also rendered. This caused leftover narrow-screen offset and allowed menu layering. |
| Status | Source/UI component behavior verified; full Milestone 1 remains in progress. |
| Tests | `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false --filter "FullyQualifiedName~AuthorizedLayoutNavigationAccessibilityTests"` — **PASS**, 5/5. |
| Runtime | `docker compose -f infra/compose.yml up -d --build --no-deps gateway` — **PASS**; Gateway healthy; `/health/live` 200; anonymous `/dashboard` redirects to sign-in (302). |
| Visual verification | Authenticated browser geometry and interaction at desktop/mobile widths **NOT VERIFIED**. |
| Commit | Not committed; the shared checkout contains pre-existing staged and unstaged work. |

### Mobile protected-route navigation flash (2026-10-31)

| Field | Result |
|---|---|
| Domain / journey | Foundation, Mobile shell; navigate among authenticated screens without changing the session. |
| Defect | Each route change reset `_signedIn` and `_authenticationResolved`, temporarily showing the auth-check panel and guest Cases destination. |
| Change | Reuse a resolved session during route changes; keep fresh fail-closed checks on initial unresolved state and actual authentication-state changes. |
| Status | Source fixed; Android build verified; device journey remains NOT VERIFIED. |
| Check | Android Debug build PASS, 0 warnings/errors; `adb devices -l` found no attached devices. |
| Visual verification | NOT VERIFIED on emulator/device. |
| Commit | Not committed; broad pre-existing staged and unstaged edits remain in the shared checkout. |

### Responsive authorized drawer correction (2026-10-31)

| Field | Result |
|---|---|
| Domain / journey | Authorized web shell; resize below/above MudBlazor Md and open/close navigation. |
| Defect | Two MudDrawers were rendered under MudHidden. The always-open persistent drawer remained part of MudLayout while visually hidden, reserving horizontal space on narrow screens. |
| Change | Replaced both instances with one `MudDrawer` using `Variant="Responsive"` and `Breakpoint="Breakpoint.Md"`, following the MudBlazor responsive drawer contract. It is persistent on desktop and temporary/overlay on smaller viewports. |
| Check | Focused drawer accessibility suite PASS, 5/5. `git diff --check` PASS after cleanup. |
| Runtime | Gateway Release Docker image rebuilt and container recreated; `/health/live` returned 200. Container health was still `starting` at the first `docker compose ps` observation. |
| Visual verification | Narrow viewport page geometry/interaction NOT VERIFIED in browser. |
| Commit | Not committed; the shared checkout contains unrelated pre-existing staged and unstaged edits. |

### Responsive authentication shell across Web and Mobile (2026-10-31)

| Field | Result |
|---|---|
| Domain / journey | M2 authentication and onboarding: sign-in, registration, recovery, verification, and account completion entry surfaces. |
| Existing implementation | Shared `AccountEntrySurface`; Gateway `PublicLayout`; Mobile `MainLayout`; web and MAUI sign-in/register/verify/welcome-complete routes. |
| Confirmed defects | Auth pages inherited the public Community/Sign-in header on Web; the shared brand panel disappeared entirely on mobile; the 48px page heading wrapped at phone widths; the auth surface could clip the guest/legal actions; unavailable providers were unreadable at MudBlazor's default disabled opacity in dark theme. Mobile auth routes also retained workspace sidebar/bottom navigation. |
| Change | Added an auth-specific back/home brand lockup using the existing SwiftAid system icon, complete brand message and guest-report path; hidden the generic Web header on identity routes; made shared auth content grow naturally and set responsive heading sizing; increased disabled provider text contrast; reused the shared surface on Mobile sign-in, registration, verification and completion routes, with workspace chrome hidden on those routes. |
| Automated check | Full `SwiftAid.Ui.Tests` **PASS**, 218/218. `git diff --check` **PASS** on scoped paths. |
| Mobile build | Android Debug build **PASS**, 0 warnings/errors. `adb devices -l` found no attached device; on-device rendering **NOT VERIFIED**. |
| Web visual check | Live Docker Gateway in browser at 390×844 and 1280×900: brand/back hierarchy, one-line phone heading, guest/report and legal actions, and no horizontal overflow **PASS**. Final disabled-provider contrast is pending the current Gateway rebuild and browser recheck. |
| Provider state | Gateway options remain configuration-backed. At the prior live probe Google/Facebook were unavailable and password sign-in was available; refresh the final live state after the rebuild before making capability claims. |
| Runtime | Release image rebuild/restart is running for the disabled-provider contrast correction. |
| Commit | Not committed; many unrelated staged and unstaged changes pre-existed in the shared checkout. |

**Known M2 gaps:** Registration still does not collect the supplied design's Full name at the credential step; no `displayName` field exists in the first-party registration endpoint request, so adding a UI field without a real persistence path would discard it. Standalone Mobile forgot/reset pages now exist and use the Gateway workflow; on-device interaction, mailbox delivery, provider flow, and end-to-end token completion remain separately unverified.

### Shared password-reset confirmation on Web and Mobile (2026-10-31)

Added a shared “Check your inbox” surface for M-AUTH-04. Web and Mobile show it only after their real Gateway request returns success; copy remains account-existence-neutral, the form is replaced by a resend action that calls the same endpoint, and resend failures remain visible without hiding the accepted-request state. The Web `/reset-password` continues to validate the returned user ID/code, fetch server password requirements, and submit confirmation through the antiforgery-protected endpoint. Mobile now has matching standalone request and reset routes and uses the same server policy and confirmation endpoint.

| Check | Result | Scope / limitation |
|---|---|---|
| Gateway Debug build | **PASS**, 0 warnings/errors | Shared Web component and Gateway page compile. |
| Android Debug build | **PASS**, 0 warnings/errors | Mobile reset routes compile for `net10.0-android`; no device attached. |
| `PublicLayoutAuthShellTests` | **PASS**, 9/9 | Includes neutral confirmation wording, resend affordance, and recoverable resend alert. |
| Docker Gateway Release rebuild/restart | **PASS** | Only Gateway recreated; container healthy, `/health/live` returned 200; served shared UI stylesheet contains the reset surface. |
| Request-form visual review | **PASS** | Rebuilt Gateway page inspected at 1280×900, 390×844 and 320×640; mobile header no longer expands into a large blank block and form content has no horizontal overflow. |
| Mailbox, token-completion, accepted-state visual review and device journey | **NOT VERIFIED** | No mailbox access/device or accepted reset request was exercised. |

### Authorized drawer recovery follow-up (2026-10-31)

The responsive drawer remains one `MudDrawer` with `Variant=Responsive` and `Breakpoint=Md`. The actual desktop regression was removal of the viewport observer that initialized `_drawerOpen` from the current breakpoint; it remained false at startup, hiding the desktop drawer. The navbar control is a MudBlazor `MudIconButton`; the restored observer opens the drawer at desktop startup, closes it on mobile, and changes that state only when crossing the breakpoint. `AuthorizedLayoutNavigationAccessibilityTests` passed 5/5, including Xs/Md/Sm transitions. The Gateway image was rebuilt/restarted; `/health/live` returned 200, the container reports healthy, and the served UI bundle includes the toggle style. Authenticated browser geometry remains NOT VERIFIED: the available browser session is anonymous and cannot render the authorized shell.

The original Ellai denial was confirmed in source: session start called a hardcoded false staff check and carried metadata that the endpoint filter always rejected. The shared `PublicLayout` mounts Ellai only inside `AuthorizeView`, while the guest WebAssembly entry has no Ellai host. The governing Ellai spec allows safe product help for guests but excludes staff tools and managed-case dossiers. Account session start now uses an identity-only zero-grant fallback when workspace scope lookup is unavailable; guest-safe session handling and the rest of the working Ellai path remain incomplete.

### Account-level Ellai session start (2026-10-31)

The authenticated HTTP and in-process session-start paths bind the current scope when available and use a short-lived identity-only scope with zero grants when scope lookup is unavailable. Session creation no longer runs through the endpoint filter's staff-only metadata denial. Privileged approval/review endpoints remain fail-closed; unknown-origin free text is still blocked before provider dispatch. This is an account-entry foundation, not a complete Ellai journey. Integration `EllaiPrivilegedRouteAuthorizationTests` PASS 12/12, including active-scope and identity-only start. The authorized navbar/drawer check passes 5/5. Guest identity/entry and safe product-help transport remain open, as do real browser and provider flow checks.

### Account session fallback and navbar drawer restoration (2026-10-31)

Current-source follow-up found ordinary account session start can receive `ScopeUnavailableException` because the existing scope source is not available outside its case-command lease. `EllaiSessionScopeResolver` now preserves a current scope when available and otherwise creates a short-lived, actor-epoch-bound scope with zero grants. `EllaiSessionService` labels this as identity-only help scope; it does not add organization, case, or tool authorization. The authenticated navbar menu is a directly rendered, keyboard-focusable button bound to the existing single `MudDrawer` using `DrawerVariant.Responsive` and `Breakpoint.Md`.

| Check | Result | Scope / limitation |
|---|---|---|
| Gateway Debug build | **PASS**, 0 warnings/errors | `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -p:WasmBuildNative=false`. |
| Integration test project build | **PASS**, 0 warnings/errors | `dotnet build tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -p:WasmBuildNative=false`. |
| `EllaiPrivilegedRouteAuthorizationTests` | **PASS**, 12/12 | Covers current scope, identity-only fallback, owner checks and privileged-route denial. |
| `AuthorizedLayoutNavigationAccessibilityTests` | **PASS**, 5/5 | Rendered layout verifies toggle/drawer relationship and Xs/Md/Sm transitions. |
| Docker Gateway Release rebuild/restart | **PASS** | `docker compose -f infra/compose.yml up -d --build --no-deps gateway`; health endpoint returned 200 and container became healthy. Browser WebAssembly publish emitted the native `e_sqlite3.a` link warning. |
| Authenticated browser and Mobile visual flow | **NOT VERIFIED** | No authenticated browser surface/device journey was exercised. |
| Ellai free-text/provider journey | **NOT VERIFIED / BLOCKED IN SOURCE** | `EllaiUtteranceSubmissionService` still rejects unknown-origin text before provider dispatch; guest and Mobile host/identity wiring remain incomplete. |

### Registration identity-name slice (2026-10-31)

Web and Mobile registration now collect a required Full name with a 128-character limit, validate on submit, and send `displayName` with the existing email/password payload. Gateway validates the value and passes it to `IPasswordIdentityRegistrar`; the registrar writes the trimmed name into `user_profiles` in the same transaction that creates the actor, access state, and Identity user. Before sign-in, Gateway reads the profile back through `IUserProfileService` and fails closed if it is absent. No registration profile is generated from the email.

| Check | Result | Scope / limitation |
|---|---|---|
| Gateway Debug build | **PASS**, 0 warnings/errors | `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -p:WasmBuildNative=false`. |
| Mobile Android Debug build | **PASS**, 0 warnings/errors | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj --no-restore -f net10.0-android`; no Android device attached. |
| Invalid display-name endpoint regression | **PASS**, 4/4 | Missing, empty, whitespace-only and overlength names are rejected before the registrar is called. |
| `IdentityEndpointSecurityTests` | **PASS**, 14 passed, 10 PostgreSQL-gated skips | Focused identity route/security suite; database-dependent cases did not run. |
| Docker Gateway Release rebuild and restart | **PASS** | Image rebuilt and only Gateway force-recreated. Container healthy; `/health/live`, `/register`, `/sign-in` returned HTTP 200. Browser SQLite native-link warning remains. |
| All-target builds | **PARTIAL** | Gateway default WASM link requires unavailable `emcc`; all-target Mobile build encountered missing cached iOS framework files. |
| Successful account submission/profile readback, rendered registration error states, and Android device flow | **NOT VERIFIED** | No account was created and no live profile database or device journey was exercised. |

### Ellai versioned product-fact read (2026-10-31)

The `ellai_productfacts` broker path no longer returns a generic success stub. It now searches a small application-owned catalog of approved nonclinical facts and returns each statement with a stable fact ID, governing spec source, and spec revision as `ProductFact` evidence. Unmatched topics and unsupported arguments are denied. The catalog supplies grounding for dynamic model responses; it does not prescribe answer text. `EllaiReadToolTests` PASS 4/4, including source/version evidence, unknown-topic denial, bounds, and canonical tool dispatch. This does not unblock user utterances: `EllaiUtteranceSubmissionService` continues to reject unknown-origin input before provider dispatch, and guest/Mobile Ellai entry plus provider/browser flow remain NOT VERIFIED.

### Ellai technical-reference approval gate (2026-10-31)

The root `docs/TECHNICAL.md` currently identifies itself as DRAFT pending maintainer review, but the previous `EllaiTechnicalDocs` reader exposed its sections to `ellai_docs_search` and `ellai_docs_get` anyway. The reader now requires an explicit document-level `**Status: Approved.**` marker before either tool exposes content. Section statuses continue to report implementation state after document approval. The checked-in DRAFT reference is therefore unavailable to the runtime tools until it is reviewed; live authorized reads and approved product facts remain separate paths. Focused documentation/read-tool tests pass 16/16. Gateway rebuild/restart and endpoint smoke are pending for this change.

### Public report validation keyboard focus (2026-10-31)

When the user selected a report subject and attempted to review an empty report, field validation displayed the missing-observation error but focus remained on the Review button. The shared `GuidedReportStepper` now makes its validation summary focusable and moves focus there after a failed review attempt; its existing heading focus remains for successful step changes. A browser acceptance assertion covers focus transfer. The interactive test-host journey verified Type → Details, then attempted Review and observed keyboard focus on “Report validation errors” with the required-observation error visible. No draft was saved and no report was submitted.

| Check | Result | Scope / limitation |
|---|---|---|
| UI test-host build | **PASS** | `dotnet build tests/SwiftAid.Ui.TestHost/SwiftAid.Ui.TestHost.csproj --no-restore -m:1 -nr:false -v:minimal`; 0 warnings/errors. |
| UI test suite | **PASS**, 219/219 | `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false`. Without the property, the Browser project attempts native link and fails because host `emcc` is missing. |
| Interactive browser focus | **PASS** | UI test host in Development on localhost; actual type selection, Continue and invalid Review; AX focused node was the validation region and screenshot showed its focus outline. Synthetic fixture only; no service or data submission. |
| Repository browser journey script | **NOT RUN** | `node tests/SwiftAid.Ui.Tests/frontend-journeys.browser.cjs` could not start: the checkout has no installed `playwright` module. Manual CUA verified this focused behavior. |
| Gateway Docker rebuild/restart after UI change | **FAIL / NOT COMPLETED** | The Compose build client was stopped after BuildKit crashed on Docker VM filesystem I/O errors. A fresh `docker info` returned HTTP 500, D: has about 7.2 MiB free, and `/health/live` times out. |

### Ellai live-provider recheck (2026-10-31)

The configured Meta reasoning and ASR adapters were retested with synthetic input only. `EllaiLiveProbeTests` PASS 3/3 with `ELLAI_LIVE=1` and values from `infra/.env` loaded into the test process without printing secrets. The reasoning request passed at the Gateway worker's 1,024-token ceiling after the earlier 512-token probe ended with `finish_reason=length`. Upload ASR processed a generated 2-second tone; realtime ASR accepted generated 24 kHz PCM. These adapter probes do not prove the blocked utterance-submission route, voice capture/playback, or full browser/mobile journeys. See `docs/ellai/TEST-EVIDENCE.md`.

### Gateway Docker rebuild failure and storage follow-up (2026-10-31)

The active `docker compose -f infra/compose.yml up -d --build --no-deps gateway` did not complete. Docker Desktop VM logs at `2026-10-31T05:34:28Z` record `Buffer I/O error` on `/dev/sdd`, EXT4 journal update failures, Docker's `/var/lib/docker` remounted read-only, failure to remove the BuildKit sandbox, and a BuildKit SIGBUS. Host API-proxy logs also report no route to `192.168.65.7:2376` and 20-second request timeouts. The Compose client was then interrupted with Ctrl+C (exit 1); no container or volume was removed. The authorized `D:\DockerAutoPrune.ps1` run reclaimed 0 bytes. At the last host check, D: had 7,544,832 bytes free and Gateway `/health/live` timed out.

| Check | Result | Scope / limitation |
|---|---|---|
| Buildx/Compose process | **STOPPED** | Stalled client was interrupted after VM logs confirmed the BuildKit worker had crashed; this did not stop Docker Desktop or other containers. |
| Docker image build / Gateway recreation | **FAIL / NOT COMPLETED** | BuildKit failed during restore/build because Docker's VM filesystem reported I/O errors and became read-only. No new image or healthy Gateway was verified. |
| Authorized Docker prune | **PASS, 0 bytes reclaimed** | Existing prune script removed one stale network; image/build-cache pruning reclaimed no space. Containers and volumes are excluded by the script. |
| Docker VM recovery / host disk recovery | **FAIL / NOT RECOVERED** | D: still has about 7.2 MiB free and Docker info returns HTTP 500. Docker Desktop restart or filesystem repair has not been attempted; do not manipulate the VHDX or remove Docker/AntHill volumes. |
| Exact `D:\CodexTemp` deletion | **NOT COMPLETED** | The execution approval review rejected the requested removal command; no deletion occurred. |

### Case-detail tab deep links and browser history (2026-10-31)

| Field | Result |
|---|---|
| Domain / journey | M3 case detail; open a case on a selected tab, change tabs, and restore selection on browser history navigation. |
| Defect | The shared detail surface kept the selected MudTab only in component state, so copied case links always reopened Overview and browser history did not restore the previously selected tab. |
| Change | Shared Gateway/Mobile case detail now reads and writes the `tab` query parameter for all seven tabs, preserving other query values and applying location changes back to MudTabs. |
| Check | `dotnet build src/SwiftAid.UI/SwiftAid.UI.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false` — PASS, 0 warnings/errors. `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false --filter 'FullyQualifiedName~CaseCorrectionWorkflowTests'` — PASS, 16/16. `git diff --check` — PASS. |
| Runtime | Gateway `/health/live` timed out after 4 seconds; fresh Docker info returned HTTP 500 and D: has 7.2 MiB free. |
| Browser / device | Real browser back/forward behavior and Android navigation remain NOT VERIFIED; the component test simulates route changes through NavigationManager. |
| Commit | Not committed; the shared checkout has unrelated staged and unstaged changes. |

### Case-history date filtering (2026-10-31)

| Field | Result |
|---|---|
| Domain / journey | M3 case detail → History; narrow the currently loaded authorized timeline by event category and the local calendar date on which each event was recorded. |
| Defect | The history view offered event-type filtering only; the case-detail contract also requires date filtering. |
| Change | Added inclusive local `Recorded from` / `Recorded through` filters, combined with existing event/related-activity filters. Invalid reversed ranges show a validation message; empty results say they apply to loaded history; clear filters resets all criteria. |
| Check | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -t:Build -f net10.0-android -c Debug --no-restore -m:1 -nr:false -v:minimal` — PASS, 0 warnings/errors. `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false --filter 'FullyQualifiedName~CaseTimelineEntryFilterTests'` — PASS, 3/3. |
| Browser / device | Responsive layout has explicit narrow, medium, and wide CSS rules; rendered browser visual/filter interaction and Android device behavior remain NOT VERIFIED because Gateway is unhealthy and no Android device was available. |
| Commit | Not committed; pre-existing staged and unstaged changes overlap repository state. |

### Case-history keyset pagination (2026-10-31)

| Field | Result |
|---|---|
| Requirement | `designs/casedetails/requirements.md` CM-M04 requires safely paginated older history while retaining authorized immutable event inspection. |
| Change | Timeline reads now fetch the newest page using a stable `AggregateVersion` cursor backed by the unique `(CaseId, AggregateVersion)` index. The Gateway validates `beforeAggregateVersion`; the shared Web/Mobile view exposes “Load older history,” merges older rows in recorded-time order, and rejects stale, non-advancing, duplicate, or malformed page responses. Guest-intake promotion appears only on the oldest page. |
| Focused checks | UI build — PASS, 0 warnings/errors. `CaseTimelinePaginationUiTests` — PASS, 2/2 (button interaction, cursor propagation, merged versions 1–4, and malformed cursor rejection). `CaseTimelineQueryTests` — PASS, 11/11, including application-layer rejection of nonpositive cursors before opening a case lease. `CaseTimelineHttpClientTests` — PASS, 8/8. `CaseTimelineEndpointTests` — PASS, 13/13. |
| Regression checks | Full `SwiftAid.Ui.Tests` — PASS, 228/228. Android Debug build — PASS, 0 warnings/errors. `git diff --check` — PASS (Git reported existing working-copy line-ending normalization notices only). |
| Runtime / persistence | PostgreSQL cursor execution, live Gateway/browser behavior, and Android device interaction remain NOT VERIFIED. Docker's data filesystem is unhealthy and the live Gateway is unavailable. |
| Commit | Not committed; shared checkout contains pre-existing staged, unstaged, and untracked work, including overlapping task paths. |

### Ellai public-help tool profile groundwork (2026-10-31)

The application carries an optional server-owned tool allowlist from queued work through reasoning; the prompt catalogue and broker both enforce it. This paragraph records the original tool-profile groundwork; subsequent guest-flow implementation and its evidence are recorded after the historical test table below.

| Check | Result | Scope / limitation |
|---|---|---|
| Focused tool profile test | **PASS**, 1/1 | `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false -v:quiet --filter 'FullyQualifiedName~SessionToolAllowlistHidesAndDeniesUnlistedTools'`; proves catalogue filtering, ProductFact access, and direct broker denial. |
| Prompt propagation test | **PASS**, 1/1 | `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false -v:minimal --filter 'FullyQualifiedName~RestrictedSessionPromptAdvertisesOnlyItsAllowedTools'`; verifies the model prompt receives only the restricted catalogue. |
| Ellai submission boundary tests | **PASS**, 8/8 | `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false -v:quiet --filter 'FullyQualifiedName~EllaiUtteranceSubmissionServiceTests'`; current policy still blocks unclassified text and preserves ownership/version checks. |
| Application test suite | **PASS**, 512 passed, 3 skipped | `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false -v:minimal`. The skipped tests are explicitly opted-in live provider probes; they were not rerun in this continuation. |
| Gateway Debug build | **PASS**, 0 warnings/errors | `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false`. |
| Ellai endpoint and authorization tests | **PASS**, 13/13 | `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false --filter 'FullyQualifiedName~EllaiPrivilegedRouteAuthorizationTests|FullyQualifiedName~EllaiEndpointMetadataTests'`; the metadata-test host now registers the scope resolver used by the production route. |
| Docker Gateway and live Ellai | **NOT VERIFIED** | Docker info hangs/returns HTTP 500, D: has about 7.2 MiB free, and the Gateway on port 8080 times out. No image rebuild or container restart was attempted in this continuation. |

### Ellai guest public-help integration update (2026-10-31)

Guest typed public-help uses Ask-only sessions, explicit session consent, classified text, and the sanitized outbox with the `ellai_productfacts` allowlist. Anonymous Blazor Server public pages use a circuit-scoped server-created guest identity and empty workspace scope. Guest UI observations are denied; approval, receipt, case and organizational tools remain unavailable.

| Check | Result | Scope / limitation |
|---|---|---|
| UI and Gateway builds | **PASS**, 0 warnings/errors each | `dotnet build src/SwiftAid.UI/SwiftAid.UI.csproj --no-restore -m:1 -nr:false -v:minimal`; `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -m:1 -nr:false -v:minimal -p:BuildProjectReferences=false`. |
| Anonymous gateway client | **PASS**, 1/1 | `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:BuildProjectReferences=false --filter FullyQualifiedName~AnonymousInProcessClientCanUseOnlyScopedPublicHelp`; covers Ask-only, empty scope, consent, sanitized outbox and observation denial. |
| Ellai endpoint/in-process authorization | **PASS**, 15/15 | `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false -v:minimal --no-build --filter FullyQualifiedName~EllaiPrivilegedRouteAuthorizationTests`. |
| Public layout rendering | **PASS**, 9/9 | `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:BuildProjectReferences=false --filter FullyQualifiedName~PublicLayoutAuthShellTests`; verifies public launcher render and identity-route suppression. |
| Docker rebuild/restart and live Ellai | **NOT VERIFIED** | At the current check `D:` has 7,544,832 bytes free and `com.docker.service` is stopped. No image build or container restart was performed. |
| Browser / provider / database | **NOT VERIFIED** | No browser-rendered guest circuit, real provider dispatch, PostgreSQL migration application or live Docker journey was run. |
| Commit | Not committed | The shared checkout has pre-existing staged, unstaged and untracked work; task paths overlap, so changes were preserved in place. |

### MAUI Ellai guest/account entry source (2026-10-31)

The shared Ellai host now renders from Mobile `MainLayout`. Guests see Ask only; accepted guest sessions use the guest cookie/antiforgery route. Signed-in users see the same Ask-only Mobile launcher for now; the authenticated transport supports the current native bearer or password-session cookie. The cookie relay keeps the existing account jar authoritative and uses a separate bearer-free guest client. Dynamic guidance has no Mobile remote-control contract yet, and microphone/TTS flows remain incomplete.

| Check | Result | Scope / limitation |
|---|---|---|
| Android Debug build | **PASS**, 0 warnings/errors | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -t:Build -f net10.0-android -c Debug --no-restore -m:1 -nr:false -v:minimal`. |
| Android runtime | **NOT VERIFIED** | `adb devices -l` returned no devices. No Mobile UI or cookie flow was rendered on-device. |
| Live Gateway | **NOT VERIFIED** | `/health/live` on `127.0.0.1:8080` timed out; Docker service stopped and D: remains nearly full. |
| Commit | Not committed | Shared checkout contains overlapping staged/unstaged work. |

### Ellai voice-control honesty and wiring audit (2026-10-31)

The shared compact composer previously rendered its microphone control from a permission flag alone, even if the host supplied no voice-start callback. Its stop control likewise depended only on a speaking flag, and the shared host did not pass a stop callback. The composer now renders those controls only when both the capability state and corresponding handler exist; the host exposes and forwards an explicit stop callback/state for a real voice runtime. Its accessible name is now “Ellai voice and text controls,” not “assistant composer.” This prevents inert controls but does not implement voice transport: the app hosts still register no conversational microphone capture, ASR-final dispatch, playback, or TTS integration. The existing provider ASR probe is not UI integration.

| Check | Result | Scope / limitation |
|---|---|---|
| Focused Ellai composer UI test | **PASS**, 1/1 | `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -v:minimal -p:WasmBuildNative=false --filter FullyQualifiedName~ComposerKeepsSendDisabledWhenHostCannotSubmit`; verifies the voice controls are absent when no handlers are wired and the named region is exposed. |
| UI source whitespace | **PASS** | `git diff --check -- src/SwiftAid.UI/Ellai/Components/EllaiCompactComposer.razor src/SwiftAid.UI/Ellai/Guidance/EllaiGuidanceHost.razor tests/SwiftAid.Ui.Tests/EllaiSurfaceLogicTests.cs`; no whitespace errors (Git reports existing line-ending conversion warnings). |
| Live voice / browser / Android | **NOT VERIFIED** | No app-level capture, session-bound ASR final-turn path, synthesis playback, or native device journey is wired. Docker/Gateway unavailable. |
| Docker cleanup/rebuild | **BLOCKED** | D: has 7,544,832 bytes free. `D:\CodexTemp` contains 366,087,640 bytes; automatic action review rejected its removal. The authorized prune previously reclaimed 0 B and the latest attempt failed against Docker Engine HTTP 500. No alternate deletion path was attempted; volumes and AntHill data remain untouched. |

Post-patch verification for the same Ellai composer slice: the focused renderer test passed 1/1 after setting microphone and speaking capability flags true without supplying callbacks; the full UI suite passed 229/229 with `-p:WasmBuildNative=false`; Android Debug target build passed with 0 warnings/errors. These remain source/build checks; they do not verify microphone permission prompts, provider dispatch from a host, TTS playback, or runtime layout on a device.

### Mobile sign-in design alignment (2026-10-31)

Mobile `/sign-in` now places “Remember me” and “Forgot password?” in one aligned row above the primary Sign in action, matching the supplied onboarding screen and the Gateway form hierarchy. Authentication routes and handlers are unchanged. Android Debug target build passed with 0 warnings/errors; `git diff --check` passed. Runtime Android/browser visual comparison and keyboard behavior remain NOT VERIFIED.

### Ellai current-turn response delivery (2026-10-31)

The worker previously completed accepted utterances after reasoning but discarded `EllaiDecision.DisplayText`, leaving the compact Web/Mobile surfaces with progress-only status and no answer. Eligible completed output now flows through a bounded one-hour, session-scoped in-memory store and a separate response cursor into the existing compact status slot. Publication is limited to consented public product help with safe classifications and a still-active session whose owner and interaction/scope epochs match. The response store is cleared on cancellation and deduplicates retries by the durable client-utterance ID. Owner checks protect authenticated routes; guest reads pass the existing cookie/owner filter. Model text remains out of the event journal; no conversation page/feed was added.

| Check | Result |
|---|---|
| Application runner/store tests | **PASS**, 7/7 — display text propagation, bounds, session separation, clearing and retry idempotency. |
| Shared UI poller tests | **PASS**, 44/44 — separate response cursor, deduplication and wire URL/DTO. |
| Authenticated/guest endpoint authorization tests | **PASS**, 17/17 — owner can read; foreign owner is hidden; guest cookie reads its own current response; cancellation clears it. |
| Android Debug target build | **PASS**, 0 warnings/errors; source/build only. |
| Integration build attempt without `-p:WasmBuildNative=false` | **FAIL**, Browser WebAssembly native link could not find `emcc`; corrected invocation with the property above passed 17/17. |
| Live provider response through the active UI | **NOT VERIFIED** — Docker Gateway is unavailable; no browser/device completion journey was run. |
| Speech capture, ASR final-turn dispatch, TTS playback | **NOT IMPLEMENTED / NOT VERIFIED** — no active-host voice runtime; TTS remains disabled pending the exact checkpoint/rights gate. |
| Docker | **BLOCKED** — D: had 7,544,832 bytes free and Docker `/info` returned HTTP 500. Automatic action review rejected removal of `D:\CodexTemp`; no bypass or Docker volume/AntHill deletion was performed. |

The shared worktree still contains overlapping pre-existing staged, unstaged and untracked changes; this slice remains uncommitted. Browser and Android runtime rendering, real model completion via the web/guest/mobile host, and audio behavior are NOT VERIFIED.

### Ellai realtime ASR completion parsing and response binding (2026-10-31)

The existing Meta realtime adapter collected only `transcript` events with `final=true`; it therefore dropped the documented completion event used by `ENDPOINTING` and `DIARIZATION` (`speechComplete` + `turnId`). It now accepts mode-appropriate finals, ignores partials and `speechEnd`, and deduplicates identified turns. The WebSocket receive now uses the linked cancellation token, so cancel can interrupt a pending receive. The compact-host test now verifies foreign-session output is ignored and a matching session result replaces status text.

| Check | Result |
|---|---|
| Application ASR protocol, runner and response-store tests | **PASS**, 13/13. Push-to-talk and endpointing finals are mode-aware; duplicate `turnId`s are ignored; overlapping out-of-order completions retain IDs and are returned in turn order. |
| UI response poller and compact-host binding tests | **PASS**, 45/45. |
| Gateway Debug build | **PASS**, 0 warnings/errors. |
| Live endpointing provider, microphone-capture pacing, host final-transcript submission, browser/Android behavior | **NOT VERIFIED** — the adapter probe is not an end-user voice journey; Docker Gateway remains unavailable. |
| MMS-TTS | **GATED** — the voice requirements require owner confirmation of the exact checkpoint and use scope before deployment; the checkpoint model card lists CC-BY-NC 4.0. |

### Current recovery verification and Docker recheck (2026-10-31)

Current reruns confirm the latest ASR parser and compact response binding, and broaden the UI regression check. No Docker-backed runtime was available.

| Check | Result |
|---|---|
| Application ASR collector, runner and response store | **PASS**, 13/13. |
| Focused shared Ellai UI poller/host binding | **PASS**, 45/45. |
| Full `SwiftAid.Ui.Tests` | **PASS**, 232/232 with `-p:WasmBuildNative=false`. |
| Ellai endpoint metadata and authenticated/guest ownership | **PASS**, 17/17 with `-p:WasmBuildNative=false`. |
| Gateway Debug build | **PASS**, 0 warnings/errors with `-p:WasmBuildNative=false`. |
| Docker service and health endpoint | **NOT VERIFIED / BLOCKED** — `com.docker.service` is stopped; `dockerDesktopLinuxEngine` previously returned HTTP 500; the current non-elevated shell was denied `Start-Service`; `127.0.0.1:8080/health/live` could not connect. D: reports 3,025,813,504 bytes free. No prune, rebuild or restart occurred in this continuation; no containers or volumes changed. |

This confirms source-level and automated UI/authorization behavior only. Authenticated browser, guest Browser/WebAssembly, Android, live provider response and Docker deployment remain NOT VERIFIED.

### Gateway public landing alignment (2026-10-31)

Anonymous Gateway `/` is selected in `App.razor` as `guest-root` and bootstraps Browser WebAssembly; `BrowserGuestRoot` renders the website through `WebLandingSurface` in `PublicWebsiteShell`. The interactive-server `Home.razor` handles signed-in `/`; its anonymous auth-state fallback previously used generic `PublicHomeSurface`, and now uses the same `WebLandingSurface` in `PublicLayout`. This keeps the fallback consistent across auth-state changes. The actual Browser guest runtime was not verified.

| Check | Result |
|---|---|
| Gateway Home anonymous fallback through auth-aware layout | **PASS**, focused `HtmlRenderer` regression test confirms SwiftAid landing sections and no authorized drawer; this is not the normal Browser guest entry path. |
| Full `SwiftAid.Ui.Tests` | **PASS**, 233/233 with `-p:WasmBuildNative=false`. |
| Gateway Debug build | **PASS**, 0 warnings/errors. |
| Live visual/browser acceptance | **NOT VERIFIED** — the normal anonymous route boots Browser WebAssembly. A raw-DLL local host returned 200 for the shell but 500 for the WebAssembly loader because the Debug app tree lacks published assets; `dotnet run` without `infra/.env` failed service validation. Both task-created hosts were stopped. Docker/Gateway remains unavailable; no viewport, theme, keyboard or real route check ran. |

This aligns the Gateway Home fallback with the Browser guest website. It does not establish the production anonymous root at runtime or close the three-reference visual parity and complete web/mobile journey gates.

## Remembered sign-in and Gateway voice-delivery update (2026-10-31)

The browser cookie-consent choice is stored as `swiftaid.cookie-preference.v1=essential-only` in localStorage. Web password sign-in defaults “Remember me” on and the Gateway auth cookie is configured for 30 days. Mobile password sign-in previously held its Gateway cookie only in the process-local `CookieContainer`; it now snapshots only a persistent HTTPS host-only `__Host-SwiftAid.Auth` cookie into MAUI `SecureStorage`, restores it for the same Gateway origin before authenticated use, refreshes it when the server renews the cookie, and clears both copies through the shared sign-out path. Session-only cookies, non-HTTPS cookies, mismatched origins, and expired values are not restored.

| Check | Command / environment | Result |
|---|---|---|
| Cookie snapshot policy | `dotnet test tests/SwiftAid.Client.Tests/SwiftAid.Client.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~GatewayAuthCookieSnapshotTests" -v:minimal` | **PASS**, 4/4; persistent/security flags, expiration, HTTPS and origin binding covered. |
| Android target compile | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -c Debug -f net10.0-android --no-restore -m:1 -nr:false -v:minimal` | **PASS**, 0 warnings/errors. |
| Sign-in persistence on a device / live Gateway | Real credential login, process restart and sign-out against configured Gateway | **NOT VERIFIED**; no Android device or live Gateway is available in this check. SecureStorage runtime behavior and actual account session continuity still need device evidence. |

Voice product direction is now Gateway-hosted synthesis streamed to Web/Mobile; clients capture and play audio but do not run local or OS TTS. The owner approved noncommercial use only. Exact checkpoint/hash and deployment, attribution and distribution review remain gated; there is no implemented Gateway audio stream or active microphone-to-ASR host path yet. The empty composer shows the mic action but keeps it disabled when the host has no voice callback.

### Gateway audio direction clarification (2026-10-31)

Renamed the application text queue from `LocalNarrationQueue` to `EllaiNarrationQueue` and documented that it only coordinates validated text inside the Gateway application; it does not synthesize or play audio. P8.7 explicitly requires authenticated, bounded Gateway-to-Web/Mobile PCM streaming and prohibits local client TTS. The transport, synthesis producer, and both UI playback paths are still absent, so this is not a voice-delivery implementation.

| Check | Command / environment | Result |
|---|---|---|
| Voice queue regression | `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~EllaiVoicePipelineTests -v:minimal` | **PASS**, 3/3. |
| Gateway Debug build | `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false -v:minimal` | **PASS**, 0 warnings/errors. |
| Default Gateway Debug build | `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -m:1 -nr:false -v:minimal` | **FAIL**, Browser WebAssembly native link cannot find `emcc` (exit 9009); the corrected build above compiles all managed projects. |
| Android Debug build | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj --no-restore -m:1 -nr:false -f net10.0-android -c Debug -v:minimal` | **PASS**, 0 warnings/errors. |
| Live Gateway | `Invoke-WebRequest http://127.0.0.1:8080/health/live -TimeoutSec 3` | **NOT AVAILABLE**, connection actively refused. |

Docker was not invoked, rebuilt, or restarted per the user's image-compaction instruction. Live browser/device audio and model synthesis remain NOT VERIFIED; model deployment remains gated on exact checkpoint/hash and obligations review.

### Guest Ellai demo and live Gateway checkpoint (2026-10-31)

Docker was restored and the user authorized Gateway rebuild/restart. The public guest FAB now opens its MudFabMenu after removing redundant controlled-open state. A live browser guest session starts successfully. An initial public SwiftAid question was incorrectly blocked because the safe-question classifier lacked the word `here` and an overview topic/fact; both were added with spec provenance. After rebuilding, the same public question completed and returned the catalog-grounded SwiftAid overview. The empty composer now uses the Material `RecordVoiceOver` live-speech icon instead of the plain microphone; it accurately remains disabled because the host has no live-voice callback.

| Check | Command / environment | Result |
|---|---|---|
| Ellai classifier regressions | `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~EllaiPublicHelpClassifierTests -v:minimal` | **PASS**, 10/10. |
| Ellai UI regressions | `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter FullyQualifiedName~EllaiSurfaceLogicTests -v:minimal` | **PASS**, 32/32. |
| Docker image | `docker compose -f infra/compose.yml build gateway` | **PASS**, Release image rebuilt. Existing WASM native-link warning remains because `emcc` is not present in the build image. |
| Gateway restart and health | `docker compose -f infra/compose.yml up -d --no-deps --force-recreate gateway`; `Invoke-WebRequest http://127.0.0.1:8080/health/live` | **PASS**, Gateway healthy, HTTP 200. No volumes or other services were touched. |
| Live guest Ellai | Browser at `http://127.0.0.1:8080/`: open guest FAB, start Ask session, consent to public product-question processing, submit `What is SwiftAid and what can I do here?` | **PASS**, initial classifier false rejection corrected; received sourced product-fact text. |
| Live speech | Same browser session | **NOT AVAILABLE**; UI explicitly says live voice is unavailable. Gateway TTS synthesis/PCM transport, web playback, microphone capture, and mobile playback remain unimplemented. |
| Demo/presentation onboarding | Required Ellai/Bonboarding gates in `docs/ellai/ellai-implementation.md` | **PARTIAL / NOT COMPLETE**; live text Ask is working, while the six-mode guidance journey, card-attached composer, voice pipeline, and full ELL-T25–38 / ELL-AT121–140 acceptance remain open. |

### Android Debug Gateway connectivity and active-voice greeting (2026-10-31)

Android Debug previously displayed the offline-drafts warning even though the Gateway was healthy on the host. The emulator could reach `10.0.2.2:8080`, but Gateway host filtering rejected that Host header with HTTP 400. The local Compose Gateway allowlist now includes the Android emulator address. Android Debug explicitly permits HTTP only for emulator builds across the notification, Datasync-spike and guest-intake clients; default client validation and Android Release remain HTTPS-only. The installed app now renders the onboarding country selector and no longer shows the offline banner.

The fixed listening greeting is now exactly “Hi, What's on your mind!” in the Gateway synthesis request and the visible active status. Gateway was rebuilt and only its container recreated. The first hand-written guest-session probe returned HTTP 500 because it encoded the `Ask` enum as a string; the API contract expects its numeric value. Repeating with the correct payload created a guest session, returned greeting PCM, and cancelled the session successfully. This verifies Gateway synthesis for the corrected fixed greeting, not audible browser or Android speaker playback.

| Check | Command / environment | Result |
|---|---|---|
| Android Debug compile and emulator install | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -t:Install -f net10.0-android -c Debug --no-restore -m:1 -nr:false -p:Device=emulator-5554 -v:minimal` | **PASS**, 0 warnings/errors; updated app deployed to `emulator-5554`. |
| App launch and onboarding render | Android 16 emulator, `adb shell monkey -p online.swiftaid.mobile 1`; screenshot `C:\Users\Vuminkosi\.codex\visualizations\2026\10\30\01a18e4c-e11d-79c2-98eb-1faae4c45a7\swiftaid-connected.png` | **PASS**, process remained active, no startup exception, offline banner cleared and country selector populated. |
| Emulator-to-Gateway reachability | `adb shell` raw HTTP GET to `10.0.2.2:8080/health/live`; Gateway container logs | **PASS**, HTTP 200 after the local Compose `AllowedHosts` update; before the update, same route was HTTP 400 Invalid Hostname. |
| Gateway image and restart | `docker compose --env-file infra/.env -f infra/compose.yml build gateway`; `docker compose --env-file infra/.env -f infra/compose.yml up -d --no-deps --force-recreate gateway` | **PASS**, Gateway Release image rebuilt and Gateway alone recreated; host `/health/live` returned HTTP 200. No volume or other container was changed. |
| Guest greeting-audio smoke | Local Gateway, antiforgery-backed guest session with numeric `Ask` enum, greeting-audio GET, then cancellation | **PASS**, session active; audio GET 200, `application/vnd.swiftaid.pcm`, 59,392 bytes, voice `swiftaid-mms-english-v1`; cancellation 200. The first malformed string-enum probe returned 500 and was not an app failure. |
| Audible browser or Android greeting playback | Actual speaker output from an active Ellai session | **NOT VERIFIED**. |
| Automated tests | Not run in this checkpoint | **NOT RUN**. |

### Android local-development sign-in recovery (2026-10-31)

The Android emulator uses the HTTP-only local Compose Gateway. Mobile password sign-in previously attempted to persist the Gateway auth cookie even when “Remember me” was off; when it was on by default, the strict HTTPS-only device persistence rejected the cookie and logged the user back out. The sign-in page now disables remembered sign-in over HTTP, starts with it off, and explains that this local session ends when the app closes. The auth client persists the cookie only when the user opted in. Compose explicitly enables a Development-only, SameAsRequest cookie policy and uses non-`__Host-` cookie names for that local HTTP path; production remains on Secure `__Host-` cookies. The mobile cookie session recognizes the Development auth cookie for its in-memory signed-in state, while secure persistence still recognizes only the production cookie.

| Check | Command / environment | Result |
|---|---|---|
| Infrastructure build | `dotnet build src/SwiftAid.Infrastructure/SwiftAid.Infrastructure.csproj --no-restore -m:1 -nr:false -v:minimal` | **PASS**, 0 warnings/errors. |
| Android Debug build | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj -t:Install -f net10.0-android -c Debug --no-restore -m:1 -nr:false -p:Device=emulator-5554 -v:minimal` | **PASS**, 0 warnings/errors; APK installed with `adb install -r`. |
| Gateway image and Development configuration | `docker compose --env-file infra/.env -f infra/compose.yml build gateway`; `up -d --no-deps --force-recreate gateway`; `docker exec gateway printenv Authentication__AllowInsecureDevelopmentCookies ASPNETCORE_ENVIRONMENT` | **PASS**, image rebuilt, only Gateway recreated, switch `true` under `Development`; `/health/live` returned HTTP 200. |
| Android app launch | `adb shell monkey -p online.swiftaid.mobile 1`; emulator screenshot | **PASS**, app launched and rendered the public home. The sign-in route itself and authentication-cookie exchange were **NOT VERIFIED**; no credentials were supplied or submitted. |
| Automated tests | Not run in this checkpoint | **NOT RUN**. |

### Shared local appearance preferences loaded by every host (2026-10-31)

The appearance and accessibility controls stored theme, reduced-motion, and text-scale choices in browser localStorage, but the shell only applied them after opening the account settings surface. The shared pre-paint preference bootstrap now runs from Gateway `App.razor` and the MAUI WebView `index.html`, applying each saved preference before the application layout is rendered. Theme loading uses `swiftaid-theme` as the canonical key, migrates the legacy key when no valid current value exists, and honors a stored `system` choice over stale legacy data. In-page settings continue applying changes immediately through the shared theme provider.

| Check | Command / environment | Result |
|---|---|---|
| JavaScript syntax | `node --check` for bootstrap and `node --input-type=module --check` for theme modules | **PASS**. |
| Gateway Debug build | `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false -v:q` | **PASS**, 0 warnings/errors. |
| Android Debug build | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj --no-restore -m:1 -nr:false -f net10.0-android -c Debug -v:q` | **PASS**, 0 warnings/errors. |
| UI regression suite | `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false -v:minimal` | **PASS**, 241/241. The default native WebAssembly link path fails because `emcc` is unavailable to the host command; the explicit property avoids that unrelated linker step. |
| Gateway image and restart | `docker compose --env-file infra/.env -f infra/compose.yml build gateway`; `docker compose --env-file infra/.env -f infra/compose.yml up -d --no-deps --force-recreate gateway` | **PASS**, Release image rebuilt and only Gateway recreated. Build emitted the known warning that native Browser SQLite assets are not linked with `WasmBuildNative=false`; no other services or volumes were changed. |
| Live Gateway and bootstrap asset | HTTP GET `http://127.0.0.1:8080/health/live` and `/_content/SwiftAid.UI/js/theme-preference-bootstrap.js` | **PASS**, both HTTP 200; served script contains `swiftaid-theme`, `swiftaid-motion`, and `swiftaid-text-scale`. |
| Browser visual preference rendering | In-app browser inspection | **NOT VERIFIED**; browser control failed before the updated page could be inspected. |

### Live case-queue lifecycle correction (2026-10-31)

| Domain | Journey | Existing implementation | Defect / change | Status | Verification |
|---|---|---|---|---|---|
| Case management | Signed-in Gateway → My cases → initial load → empty/available/error state | `src/SwiftAid.Gateway/Components/Pages/MyCases.razor`, `src/SwiftAid.UI/Components/Surfaces/Operations/OperationsCaseQueue.razor`, `ICaseWorkspaceClient` | The initial asynchronous load runs from `OnAfterRenderAsync`; without an explicit final render, completed request state remained visually stuck on Loading until user input. Added generation-guarded `StateHasChanged` in `finally`. | **Verified live** | UI suite 241/241, Gateway Debug build passed, rebuilt/restarted Gateway, live browser reload displayed “No matching cases” and filters without Retry. Underlying user scope currently has no cases. |

The Dashboard aggregates loaded in the same browser session and displayed zero cases plus source-honest unavailable metrics. Surveillance remained unavailable because there is no available aggregate map data in the live view; no local fake data was introduced. A live Ellai Ask activation and consent-denial path was verified; provider processing was correctly not invoked after “Not now.” Android route/runtime and visual navigation-flash acceptance remain open.

### Signed-in homepage keeps the public website surface (2026-10-31)

The Gateway root page previously changed into `PublicHomeSurface` inside the authorized workspace layout for signed-in users. That contradicted the website requirement and exposed the staff sidebar on `/`; the website header also continued to say “Sign in.” The root now uses `PublicWebsiteLayout` for both authentication states. It keeps the SwiftAid landing page and community navigation, displays “Workspace” for signed-in users and “Sign in” for signed-out users, and changes the landing workspace action to “Open your workspace” for signed-in users. The authorized/public Ellai menu modes remain distinct. Staff destinations such as `/dashboard` continue using the authorized workspace shell.

| Check | Command / environment | Result |
|---|---|---|
| Gateway Home auth/layout UI tests | `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false -v:minimal --filter FullyQualifiedName~PublicLayoutAuthShellTests` | **PASS**, 11/11; anonymous and authenticated website navigation are asserted without the authorized sidebar. |
| Full UI suite | `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false -v:q` | **PASS**, 242/242. |
| Gateway Debug build | `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false -v:q` | **PASS**, 0 warnings/errors. |
| Gateway image and restart | `docker compose --env-file infra/.env -f infra/compose.yml build gateway`; `up -d --no-deps --force-recreate gateway`; `GET /health/live` | **PASS**, only Gateway rebuilt/recreated, health HTTP 200; known native Browser SQLite linker warning remains. |
| Live signed-in root | Authenticated in-app browser at `http://127.0.0.1:8080/` after Gateway restart | **PASS**, community and Workspace links visible, full SwiftAid landing content rendered, no authorized sidebar, workspace CTA points to `/dashboard`. |
| Live anonymous browser root, mobile responsive viewport, navigation animation trace | No anonymous profile or controlled viewport/frame trace in this checkpoint | **NOT VERIFIED**; anonymous behavior is covered by component test only. |

Cross-host follow-up for the same checkpoint: `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj --no-restore -m:1 -nr:false -f net10.0-android -c Debug -v:q` **PASS**, 0 warnings/errors. This is compile evidence only; no Android visual journey was run for the public website layout change.

### Local appearance preferences reapply across layouts (2026-10-31)

The pre-paint bootstrap already loads saved theme, motion, and text scale. The shared interactive theme provider now reapplies all three when a layout initializes, listens for same-tab preference events and cross-tab localStorage changes, and refreshes reduced-motion behavior when the device setting changes. The motion and text scale controls publish the shared event after saving. This covers Gateway, MAUI, and the Browser guest root, which all mount the shared provider. The preference keys remain origin-local in web browsers.

| Check | Command / environment | Result |
|---|---|---|
| JavaScript syntax | PowerShell `Get-Content -Raw` piped to `node --input-type=module --check` for the shared provider, motion, and text-scale modules | **PASS**. |
| Gateway Debug build | `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -m:1 -nr:false -c Debug -p:WasmBuildNative=false -v:q` | **PASS**, 0 warnings/errors. The initial default build failed because `emcc` is unavailable; the explicit host-supported setting passed. |
| Android Debug build | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj --no-restore -m:1 -nr:false -f net10.0-android -c Debug -v:q` | **PASS**, 0 warnings/errors. |
| Gateway image and restart | `docker compose --env-file infra/.env -f infra/compose.yml build gateway`; `up -d --no-deps --force-recreate gateway` | **PASS**, Gateway rebuilt and only Gateway recreated. Known native Browser SQLite linker warning remains; no volumes changed. |
| Live Gateway and served provider module | GET `http://127.0.0.1:8080/health/live` and `/_content/SwiftAid.UI/Components/SwiftAidThemeProvider.razor.js` | **PASS**, both HTTP 200; served module contains the shared preference event and text-scale key. |
| Saved preference visual/interaction journey | Change theme, motion, and text size, then navigate between layouts on web and MAUI | **NOT VERIFIED** in an authenticated browser/device interaction. |

### Live-speak microphone-level feedback (2026-10-31)

The compact composer now keeps the active GraphicEq control visible and turns it into the stop action while realtime capture is active. The shared capture processor measures microphone RMS per audio block and adjusts the control's glow directly from that level; recorded-transcription capture does not enable the indicator. Reduced-motion mode holds a steady active glow. Capture stop/cancel removes the temporary glow styles. This implements the existing audio-level feedback seam in `docs/ellai/ellai-implementation.md` and keeps the feedback in the single composer.

| Check | Command / environment | Result |
|---|---|---|
| JavaScript syntax | `Get-Content -Raw src/SwiftAid.UI/wwwroot/js/ellai-voice-capture.js | node --input-type=module --check` | **PASS**. |
| Gateway Debug build | `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -m:1 -nr:false -c Debug -p:WasmBuildNative=false -v:q` | **PASS**, 0 warnings/errors. |
| Android Debug build | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj --no-restore -m:1 -nr:false -f net10.0-android -c Debug -v:q` | **PASS**, 0 warnings/errors. |
| Live Gateway asset after rebuild | GET `http://127.0.0.1:8080/health/live` and `/_content/SwiftAid.UI/js/ellai-voice-capture.js`; Compose health | **PASS**, both HTTP 200, served capture script includes level update and stop cleanup, Gateway container healthy. |
| Live microphone-level visual behavior, silence/noisy input, cancel cleanup, and reduced-motion rendering | No authenticated browser/device voice session exercised | **NOT VERIFIED**. |

### Voice feedback, mobile session persistence, and preference wiring (2026-10-31)

The LiveSpeak glow remains driven by microphone RMS for both web and MAUI. Reduced-motion preference now disables the CSS transition while preserving level-responsive glow; this supersedes the earlier note that described a steady glow under reduced motion. The shared UI provider and pre-paint bootstrap are mounted in the Gateway public and authorized layouts, the MAUI main layout, and the Browser guest root. Saved theme, motion, and text-scale preferences remain browser-origin-local, so HTTP and HTTPS have separate web storage; a live preference-change/navigation journey was not exercised in this checkpoint.

Mobile remembered sign-in had a validator mismatch: `GatewayAuthCookieSnapshot` accepted a secure production HTTPS cookie and the explicitly configured loopback/Android-emulator development cookie, while `SecureGatewayAuthCookiePersistence.WriteAsync` rejected all HTTP. The writer now delegates validation to the same `RestoreFor` policy, preserving host, cookie-name, transport, value, and expiry checks.

| Check | Command / environment | Result |
|---|---|---|
| LiveSpeak module syntax | `Get-Content -Raw src/SwiftAid.UI/wwwroot/js/ellai-voice-capture.js | node --input-type=module --check` | **PASS**. |
| Gateway Debug build | `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -c Debug -p:WasmBuildNative=false` | **PASS**, 0 warnings/errors. |
| Android Debug build | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj --no-restore -c Debug -f net10.0-android` | **PASS**, 0 warnings/errors. |
| Gateway image and restart | `docker compose --env-file infra/.env -f infra/compose.yml build gateway`; `up -d --no-deps gateway` | **PASS**, only Gateway rebuilt/recreated; existing native Browser SQLite linker warning during publish; health endpoint returned HTTP 200 and container healthy. |
| Served voice-capture module | GET `http://127.0.0.1:8080/_content/SwiftAid.UI/js/ellai-voice-capture.js` | **PASS**, HTTP 200; served module contains level-based normalization and no fixed reduced-motion level. |
| Cookie snapshot policy | `dotnet test tests/SwiftAid.Client.Tests/SwiftAid.Client.Tests.csproj --no-restore -m:1 --filter "FullyQualifiedName~GatewayAuthCookieSnapshotTests" -v:minimal` | **PASS**, 6/6. |
| Secure-storage Android build after HTTP policy correction | Android Debug build above | **PASS**, 0 warnings/errors. |
| Live voice response to speech level on web/Android | No live microphone session exercised | **NOT VERIFIED**. |
| Remembered sign-in across app restart on HTTP and HTTPS | No credential login/restart journey exercised; Android package was not reinstalled | **NOT VERIFIED**. |
| Visual loading of saved preferences across page/layout navigation | Source wiring inspected; no interactive preference journey run | **NOT VERIFIED**. |

### Mobile Agentic UI theme parity (2026-10-31)

Applied the Agentic UI contract to the mobile host and shared surfaces. The mobile shell, startup canvas, Android system bars, navigation, public action cards, report status surfaces, and overlays now consume the shared semantic theme tokens instead of fixed light/blue values. The theme root uses the contract's `data-theme="agentic"` and `data-color-mode="light|dark"` attributes, matching web. Android light and dark mode were reviewed on the emulator; the Account surface exposes System, Light, and Dark controls and uses the same theme preference provider. The Android screenshot at `C:\Users\Vuminkosi\.codex\visualizations\2026\10\31\mobile-account-theme.png` shows the account preference surface and shared green-led styling.

| Check | Command / environment | Result |
|---|---|---|
| Gateway Debug build | `dotnet build src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --no-restore -c Debug -p:WasmBuildNative=false` | **PASS**, 0 warnings/errors. |
| Android Debug build | `dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj --no-restore -c Debug -f net10.0-android` | **PASS**, 0 warnings/errors. |
| Appearance surface regression | `dotnet test tests/SwiftAid.Ui.Tests/SwiftAid.Ui.Tests.csproj --no-restore -m:1 -nr:false -p:WasmBuildNative=false --filter "FullyQualifiedName~AppearanceAboutSurfaceTests" -v:minimal` | **PASS**, 4/4. |
| Android visual theme | Android emulator `emulator-5554`, light and dark system modes | **PASS**, public landing and account surfaces captured; light/dark colors, shell, and navigation follow the selected mode. Device mode restored to light. |
| Gateway image/restart and live health | Compose Gateway build/recreate; `GET http://127.0.0.1:8080/health/live` | **PASS**, image rebuilt, Gateway healthy, health endpoint HTTP 200. |
| Saved theme selection applying through mobile navigation and process restart | No interactive preference-change/restart journey run | **NOT VERIFIED**; source wiring and controls are present. |
| Full Android accessibility viewport/text-scale review | Not exercised | **NOT VERIFIED**. |
| Remembered sign-in across Android restart | No credential login/restart performed; current app surface showed Guest | **NOT VERIFIED**. The HTTP development-cookie writer fix is covered by policy tests and builds, but no persisted authenticated session was established. |

### Android guest Ellai runtime probe (2026-10-31)

A live Android emulator probe used the installed Debug app at the guest `/account` surface and the local Gateway at `http://127.0.0.1:8080`. The shared launcher opened the compact composer and an Ask session was created. A public SwiftAid product question reached the purpose-specific processing dialog; after allowing the one-hour grant, the guest utterance POST returned HTTP 200 and the app showed active progress. Gateway event and response polling returned HTTP 200. The visible status remained “Reviewing the evidence…” for approximately 30 seconds without a final answer. Cancel then removed the composer. No microphone capture or private/case data was used. This confirms the Android guest entry, session, consent and utterance transport only; the final model response through the host is **NOT VERIFIED** and needs investigation.

The probe also showed the current in-flow public-help consent dialog, while the owner previously requested Ellai consent choices be managed in Settings/About by default. The canonical Ellai permission contract still requires purpose-specific, non-preselected consent where applicable. Treat this as a product-policy discrepancy to reconcile before changing server grants; do not substitute a local UI toggle for an authoritative grant.

Evidence: emulator captures `C:\Users\Vuminkosi\.codex\visualizations\2026\10\31\swiftaid-ellai-open2.png`, `swiftaid-ellai-send.png`, `swiftaid-ellai-response.png`, and `swiftaid-ellai-cancelled.png`; Gateway logs show guest session creation, consent POST, utterance POST and successful event/response polls. The transient session was cancelled.
