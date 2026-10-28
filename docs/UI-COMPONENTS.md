# UI component decision register

Approved owner decision (2026-10-26): **MudBlazor is primary** for ordinary workflows and the shared visual system. **Syncfusion is reserved for justified complex specialized components** where a concrete requirement benefits from it. Both must follow one SwiftAid token system; MudBlazor owns shared visual identity. This does not change product requirements.

| Surface / use | Provider | Decision / gate | Package and license evidence | Android/web result |
|---|---|---|---|---|
| App shell, navigation, forms, validation, dialogs, notifications, cards, tabs, lists, standard tables, responsive and accessibility states | MudBlazor | Primary/default | Directory.Packages.props declares MudBlazor 9.11.0 in the observed scaffold; restore compatibility and license/package review not verified. | NOT VERIFIED |
| Advanced case queue grid, multi-resource scheduler, complex interactive analytics, map/geospatial visualization, or other specialist control | Syncfusion, only if requirement warrants | Conditional; do not use for ordinary controls or duplicate a MudBlazor control without a recorded decision. | No package/license evidence observed. Verify supported package versions, license eligibility/key protection before enabling. | NOT VERIFIED |

Do not treat this register as proof a control is implemented. Before a Syncfusion use, record screen, exact component, reason MudBlazor is insufficient, package version, license basis, Android and web tests, accessibility, styling, offline and performance constraints. Keep a functional MudBlazor fallback while license/compatibility is unresolved.

## Accepted asset and shared-component decisions

| Decision | State/date | Decision and scope | Current evidence / status |
|---|---|---|---|
| UI-03 | accepted, owner, 2026-10-26 | Use root `swiftaid-icon.png` to guide theme colors, Gateway favicon, and Android application-icon source. This does not change other product requirements. | Root input, `src/SwiftAid.Gateway/wwwroot/favicon.png`, and `src/SwiftAid.Mobile/Resources/AppIcon/appicon.png` are each 1,334,182-byte PNGs at 1254×1254 with SHA-256 `CA502C538415586ABE031CFCBA2B20EDF742E24002C7FB1B528F9DF5A9DF86BD`. Owner selection and matching hashes do not verify authorship/license. Mobile Release build generated launcher icon variants at 48, 72, 96, 144, and 192 pixels; device display remains NOT VERIFIED. |
| UI-04 | accepted, owner, 2026-10-26 | Share UI components across web and MAUI through a portable `SwiftAid.UI` Razor Class Library targeting `net10.0` with MudBlazor and Lottie. Move reusable loader/theme/card/status components into the library; keep server SDKs and secrets out. | `src/SwiftAid.UI` is implemented and referenced by Gateway and Mobile. Shared components include the brand theme/provider, loader, page heading, body text, and service-boundary notice. Independent code review and UX review PASS; Gateway Release and Android Release builds PASS per implementation handoff (exact Release build command strings were not recorded). Host-specific pages/layout remain local, so the full intended migration is not asserted. The earlier public-shell Chrome journey passed Home/navigation, `/sessions` → `/report`, truthful unavailable states, Mud shell, and 320px. Corrected scripts independently reviewed PASS: `shared-ui.browser.cjs` SHA-256 `9E3148060F9D50A22705F7AB4BD68B9E7687BFF35E1FDFFECCA57DEC86C6774E`; `gateway-nav.browser.cjs` SHA-256 `0085C5019CA512463FE5F29B58E002B88C99880A86960CDABDD8374A7B5D4512`. Final Node/headless-Chrome reruns exited 0. Hosts launched from the DLLs with Development/content root, child stdout-bound ephemeral ports (34390/48990 plus PID), and expected heading verified; both processes exited through `finally` and ports were free. Loader checks observed GIF then Lottie DOMLoaded SVG/canvas, reduced-motion still image, light/dark switching, 320/390/768/1280px without overflow, local assets HTTP 200 and no external requests, failures, or 404s. The malformed-payload reload case awaited the second response and asserted visible GIF/status and pending opacity 0; injected parser errors were correlated exactly, with no other page errors. Drawer mouse state matched `aria-expanded`; Space opened and Enter closed; `aria-controls` resolved the drawer. This is bounded component/navigation evidence, not full product-flow acceptance. The same-player post-ready exception branch remains source-reviewed only. Browser version, screen-reader/runtime AX, Android/device behavior, and broader application accessibility remain NOT VERIFIED. Toggle-state source S2 is resolved; this browser evidence does not establish assistive-technology behavior. |

## Palette and contrast evidence

Owner-approved brand choices include blue `#126FE1`, action teal `#00695C`, dark-theme teal `#55D5B7`, and dark-link `#80BFFF`. Named contrast calculations supplied for this snapshot are: `#126FE1` on white 4.787:1; `#00695C` on white 6.614:1; and `#55D5B7` on `#062D29` 8.189:1. Independent accessibility source review PASS reports the Home dark-secondary S2 closed at source by using the `SwiftAidBodyText` semantic token; contrast was reported as 6.55:1 in light mode and 7.73:1 in dark mode. The reviewed Gateway `MainLayout.razor` source SHA-256 is `F3C69A766735D7519903CC3379FCE6FBBB776288E545707D28572DF1238E440D`; `src/SwiftAid.UI/Theme/SwiftAidBrandTheme.cs` SHA-256 is `80B6E61A473F06427077BA97D2DEAE95E1990D98A56DB14BEBDADC5490EF2FED`. The same accessibility and code re-review closed the toggle-state S2 at source. These are source-review findings and calculated pairings, not rendered/runtime contrast acceptance. The bounded browser check verified drawer mouse/keyboard state and ARIA attributes; screen-reader behavior, Android/device acceptance, and broader accessibility remain NOT VERIFIED.

## Auxiliary loading animation

Approved owner decision (2026-10-26): use the owner-provided SwiftAid loader GIF, with optional Lottie rendering through MudXtra's standalone `Blazor.Lottie.Player` package. This is an auxiliary animation renderer; it does not replace MudBlazor as the primary UI and shared token system, alter the targeted Syncfusion decision, or change other product requirements. Current source references the package and implements reusable Gateway/Mobile loader components; this is source integration evidence, not a claim of verified runtime rendering.

The current loader component is shown only when its `IsLoading` parameter is true. The visual is marked `aria-hidden` and paired with separate status text. Source watches `prefers-reduced-motion` and uses the bundled still PNG when reduced motion applies or the preference cannot be read; the bounded browser fixture rendered that still image under reduced motion. Runtime accessibility-tree behavior was not established by that fixture. The Gateway reconnect modal uses the GIF while reconnect/retry is pending. These component behaviors do not change product requirements.

GIF, Lottie JSON, and still PNG assets are bundled under `wwwroot/assets/loading` in Gateway, Mobile, and the canonical `SwiftAid.UI` RCL. All three copy sets have matching hashes: GIF `4B28427B74D9DC564C8C78112C91F57B0CD77AC612B95B1F1D87C897FC7FD2BB`; Lottie JSON `0DE3B1CF5373A84F12D31BB2AC8A7E3012DDD14D9538B3237C5AE08D1E0DD294`; still PNG `0A77628A1871FBE7DF3270D239142DCD556C546AFD854BDAB67FC0C0448D7279`. Package references to `Blazor.Lottie.Player` 1.0.6 exist in both app projects. The current source explicitly configures the local `./_content/Blazor.Lottie.Player/lottie.min.js` file; its JS helper loads this local path and does not rely on the package's default CDN setting. This source path does not by itself prove offline runtime operation.

Implementation handoff reports Gateway Release and Android Release builds passed with zero warnings/errors and JavaScript syntax checks passed. The bounded browser fixture rendered GIF then Lottie DOMLoaded SVG/canvas and exercised reduced-motion fallback; this does not establish a full application workflow. Android device rendering, performance, and device interaction remain NOT VERIFIED. Runtime accessibility-tree behavior remains NOT VERIFIED. NuGet metadata reports `Blazor.Lottie.Player` 1.0.6 as MIT and .NET 8+ compatible; MudXtra describes it as a standalone package. The pinned repository LICENSE is MIT, but the source project declares 1.0.5, so exact equivalence with the 1.0.6 NuGet artifact is NOT VERIFIED. GIF/Lottie/icon authorship, license and redistribution rights remain NOT VERIFIED; the owner selection does not imply public redistribution rights. Asset sizes and copy/source hashes are recorded in [RIGHTS-REGISTER.md](RIGHTS-REGISTER.md).

## Lane B shared design system (2026-10-28)

Reference boards are copied byte-identical into `docs/design/references/` (SHA-256 match against `designs/` originals): `SwiftAid Health Surveillance UI Showcase.png` (`457D4E24…BE88CB`), `SwiftAid One Health UI Storyboard-1.png` (`804527EB…20DFC414`), `SwiftAid Health Management UI Showcase-2.png` (`29E6360F…FEE485CA`). Originals in `designs/` are unaltered. Tokens below were read from those three images.

### Shared token components (`src/SwiftAid.UI/Components/Theme/`)

| Component | File | Purpose | Renderer |
|---|---|---|---|
| Design tokens | `SwiftAidDesignTokens.cs` | Single source for brand hex values (`#126FE1` primary blue, `#00695C` action teal, `#55D5B7` dark teal, `#80BFFF` dark link), status-pill colors (Escalated `#B4233A`, In Progress `#075CB5`, Follow-up `#B45309`, Open/Resolved `#176B43`), chart series, map cluster colors, typography scale (12/14/16/20/24), 4-pt spacing (4/8/16/24/32), radii (12/999/8), plus `ToMudColor()` status-to-MudBlazor mapping | C# constants, no UI |
| Status chip | `SwiftAidStatusChip.razor` | Reference-style status pill (`Status` parameter, required) with `aria-label="Status: …"` | MudBlazor `MudChip` |
| Section card | `SwiftAidSectionCard.razor` | Card shell with required `Title`, optional `Subtitle`, `ChildContent`, `Actions` (Recent Alerts, Case Summary, AI Analysis Summary patterns) | MudBlazor `MudCard` |
| Stat tile | `SwiftAidStatTile.razor` | Metric tile with required `Label`/`Value`, optional `Delta`/`DeltaUp` trend (dashboard stat rows) | MudBlazor `MudPaper` + `MudText` |

`SwiftAidThemeProvider.razor` is unchanged and still owns theming via `SwiftAidBrandTheme.Create()` (light primary `#00695C`, dark primary `#55D5B7`). The new token class names values only; it does not replace the brand theme.

### Existing shared components (`src/SwiftAid.UI/Components/`)

| Component | Covers on the boards |
|---|---|
| `SwiftAidPageHeading.razor` | Page headings (Dashboard, Case List, Surveillance, Community Education, Reports & Analytics) |
| `SwiftAidBodyText.razor` | Secondary/body copy with semantic light/dark tokens |
| `SwiftAidLoader.razor` (+ boundary) | Loading/pending states (mobile offline "pending sync", submission confirmations) |
| `SwiftAidServiceBoundaryNotice.razor` | Truthful unavailable/out-of-scope states (no AI triage, no clinical advice) |
| `CaseReportRegistrationForm.razor` | Guided case capture (Report a Case / New Case Report flows) |
| `Cases/CaseTimelineView.razor` | Immutable case history timeline (Case Details screens) |
| `Coordination/IntakeCoordinationQueue.razor` | Staff intake queue (`/intake-coordination`) |
| `Notifications/NotificationFeed.razor` | Alerts/notifications (Recent Alerts, Recent Activity) |
| `Public/PublicReportForm.razor`, `PublicReportPlaceField.razor`, `PublicReportReceipt.razor`, `PublicReportDraft.cs` | Public intake form and receipt (intake receipt grants no staff access) |

### MudBlazor-vs-Syncfusion decisions

MudBlazor stays primary for every surface observed on the three boards: app shell/nav, stat tiles, cards, status pills, tabs, forms, steppers, dialogs, lists, standard tables, and simple charts. No Syncfusion package is referenced and no Syncfusion component is used. A Syncfusion control is allowed only when a concrete screen records: the screen, the exact component, why the MudBlazor equivalent is insufficient, package version, license basis and key protection, Android and web test evidence, accessibility, offline, and performance notes — with a working MudBlazor fallback kept while that gate is unresolved. The map/geospatial view, multi-resource scheduler, advanced queue grid, and complex interactive analytics on the boards are the only candidate surfaces, and none is implemented with Syncfusion in this lane.

### Tested behavior

`dotnet build` on `src/SwiftAid.UI/SwiftAid.UI.csproj` passes (result recorded in the Lane B handoff). No placeholder or lorem-ipsum text exists on any new UI surface: all new components render only caller-supplied text; defaults are real labels ("New"). Browser rendering, device display, screen-reader behavior, and rendered contrast acceptance remain NOT VERIFIED. Per-screen route mapping is in [UI-SCREEN-CHECKLIST.md](UI-SCREEN-CHECKLIST.md).

## Case monitoring command panel (P5.2, 2026-10-28)

`src/SwiftAid.UI/Components/Cases/CaseCommandPanel.razor` (shared by Gateway and Mobile case-detail pages via `ICaseCommandClient`) adds six staff operations alongside the existing six. All labels are factual staff-facing language; no clinical advice, no invented defaults (the escalation recipient route is free text with no default), no implementation jargon. Every operation requires a reason plus confirmation, following the existing Review/Confirm pattern.

| Operation | Fields | Identifier rule |
|---|---|---|
| Record observation | Observation note, source, optional observation id | Blank id generates a new key; a supplied id must parse as a GUID. |
| Schedule follow-up | Purpose, assignee id, due time (UTC, ISO format), optional follow-up id | Blank id generates a new key; due time must parse. |
| Record follow-up outcome | Follow-up id (required), outcome (Completed/Missed/Unreachable/Rescheduled/Cancelled), outcome notes, optional new due time | Follow-up id must parse; a new due time is required when rescheduling. Outcome wording never implies care was completed. |
| Raise escalation | Optional escalation id, recipient route (required, free text), optional comma-separated evidence references | Blank id generates a new key. |
| Acknowledge escalation | Escalation id (required), optional action summary | Escalation id must parse. |
| Close escalation | Escalation id (required), resolution | Escalation id must parse. |

`CaseNextActionsCard` notes now also cover scheduled follow-ups with no later outcome event, raised escalations with no later acknowledgement, and latest-observation presence; missed/unreachable/rescheduled wording never implies care completed. Timeline labels for the six new event types render via `CaseTimelineQuery.LabelFor` arms added in the queue follow-up; no placeholder text is shown.

MudBlazor remains the only component library on this panel; no Syncfusion use. Verified: `SwiftAid.UI`, Gateway, Client, and Mobile Android Release builds pass with zero warnings/errors; Client (119), Application (237), and case-command endpoint suites (43) pass. Browser rendering, device display, and screen-reader behavior remain NOT VERIFIED.

## Monitoring queue (SA-FUP-06/SA-MON-04, 2026-10-28)

`src/SwiftAid.UI/Components/Cases/MonitoringQueue.razor` (+ `.razor.css`) renders the cross-case queue: due-first open follow-ups with overdue highlighting, raised escalations, assignee, and status chips. States covered: loading, denied, error with retry, and empty (empty is a normal outcome, never proof of absence). Links open the per-case timeline behind ReadCase. Consumed by the Gateway `/monitoring` page (assigned + open sections, staff-only nav entry) and the Mobile `/monitoring` page, both through `IMonitoringQueueClient`. Queue rows carry scheduling identity and status only; narratives and sealed payloads stay on the per-case timeline.

MudBlazor only; no Syncfusion. Verified: `SwiftAid.UI`, Gateway Release, and Mobile Android Release builds pass with zero warnings/errors; Application (242), Client (127), and Integration live (388) suites pass. Browser rendering, device display, and screen-reader behavior remain NOT VERIFIED.

## Community sessions (SA-SES-01–SA-SES-04, SA-SES-09, SA-SES-13; AT-29, 2026-10-28)

P10.1 API/UI lane. The domain/contract foundation (`ICommunitySessionService`, session command hierarchy, public projection, ICS/invitation builders) is owned by the contract/domain lanes; the service implementation behind the interface is owned by the backend lane. This lane owns the Gateway endpoints, shared UI, mobile public page, HTTP client, and endpoint/client tests below. No social-platform API is used anywhere: sharing is copy-invitation text, the device share sheet, or opening the pasted channel link. Copying never marks an invitation delivered.

| Piece | File | Behavior |
|---|---|---|
| Officer endpoints (staff auth, cookie-or-bearer schemes) | `src/SwiftAid.Gateway/Endpoints/CommunitySessionEndpoints.cs` | `POST /api/v1/sessions` (create draft, server-assigned id), `PUT /api/v1/sessions/{id}` (update), `POST /api/v1/sessions/{id}/review\|schedule\|complete\|cancel`. Wire DTOs are validated (required text, future/past rules left to the service, distinct reminder leads, http(s) channel links). Stale versions → 409, state violations → 422, invalid → 400 (problem details), unauthenticated → 401, disabled pipeline → 404. |
| Public endpoints (anonymous) | `src/SwiftAid.Gateway/Endpoints/PublicSessionEndpoints.cs` | `GET /public/v1/sessions` (limit 1–100), `GET /public/v1/sessions/{id}`, `GET /public/v1/sessions/{id}/calendar` (`text/calendar` download named `session-{id}.ics`), `GET /public/v1/sessions/{id}/invitation` (`text/plain` logistics). Non-published sessions resolve to the same generic 404 as unknown ids. |
| Session editor (officer) | `src/SwiftAid.UI/Components/Sessions/SessionEditor.razor` (+ `.razor.css`, `SessionCommandInputs.cs`) | Create/edit/review/schedule/update/cancel/complete with per-field messages mirroring domain rules (required text, 1–1440 min duration, positive capacity, at least one source-linked material, distinct 1–168 h reminder leads, http(s) channel link, host-validated time zone, future start for scheduling). The self-review acknowledgement checkbox appears only when the reviewer planned the draft (PC-08). Presentational: the hosting page executes commands. |
| Public list | `src/SwiftAid.UI/Components/Sessions/PublicSessionList.razor` (+ `.razor.css`) | Published list with Scheduled/Cancelled status chips (cancelled shows the reason), calendar-file download link, copy-invitation button (clipboard with manual-select fallback), optional device share-sheet button, loading/empty/error-with-retry states. No attendee, case, or audit data is rendered. |
| Gateway page | `src/SwiftAid.Gateway/Components/Pages/Sessions.razor` (+ `src/SwiftAid.Gateway/Sessions/SessionGatewayClient.cs`) | `/sessions` serves both audiences: the public list for everyone, plus the editor inside the staff-only section. Plain-language officer outcomes; version conflicts ask for a reload. |
| Mobile page | `src/SwiftAid.Mobile/Components/Pages/Sessions.razor` (+ nav entry, `MauiProgram.cs` registration) | Public list only, through `CommunitySessionHttpClient`; the device share sheet opens from the invitation text. Officer actions on mobile remain a follow-up; nothing fakes an authenticated staff action. |
| HTTP client | `src/SwiftAid.Client/Sessions/CommunitySessionHttpClient.cs` | Public list/get/invitation/calendar reads; missing or failed reads are empty/null results, never exceptions. |
| Tests | `tests/SwiftAid.Integration.Tests/CommunitySessionEndpointTests.cs` (33), `tests/SwiftAid.Client.Tests/CommunitySessionHttpClientTests.cs` (10) | Fake-service TestServer host: auth, limit validation, 409/422 mapping, public 404s, ICS content-type with UID/SEQUENCE and filename, invitation text; client URL mapping and failure outcomes. |

MudBlazor remains the only component library on these surfaces; no Syncfusion use (a plain list/editor needs no scheduler control). Verified by the parent after the lane runners failed: `SwiftAid.UI`, Gateway Release, and Mobile Android Release builds pass with zero warnings/errors; Domain 83/83, Application 251/251, Client 137/137 (10 new client tests), Integration 429 passed / 0 failed / 55 env-gated skips on disposable PostgreSQL (28 endpoint tests + 8 live scheduling tests). Browser rendering, device display, and screen-reader behavior remain NOT VERIFIED.
