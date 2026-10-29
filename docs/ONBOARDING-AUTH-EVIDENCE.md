# Onboarding auth capability evidence — Phase 3

Date: 2026-10-29 (UTC). Scope: sign-in/sign-out identity paths used by onboarding.
Rule applied: provider integrations are **Blocked / Not provisioned** until exercised
against real credentials. Nothing below claims a live provider, device, or
browser verification that did not run.

## Backend probes (exact searches, `src/` tree)

- `ForgotPassword|ResetPassword|ConfirmEmail|CreateAsync.*User|CheckPasswordAsync|PasswordSignInAsync|GeneratePasswordResetToken|AddPasswordAsync`
  → only hit is `SwiftAidUserClaimsPrincipalFactory.CreateAsync` (claims mapping).
  **No password registration, password sign-in, reset, or email-confirmation
  backend exists.** M-AUTH-01 password fields, M-AUTH-02 create-account,
  M-AUTH-04/05 recovery, and M-AUTH-09 OTP therefore have no contract to bind
  to. Building those forms now would be fake UI, so they are **Blocked** and
  intentionally absent. The UI must not imply they exist.
- `[Rr]egister|CreateUser|PasswordHasher|SignInManager` in Gateway/Application/
  Infrastructure → `SignInManager<SwiftAidUser>` is used only by
  `IdentityEndpoints` (external-login completion, reauth, link/unlink) and
  push-device registration. No local-account registration path exists.

## Real, adopted identity paths (no new auth system)

- Gateway web: cookie sign-in via `SignIn.razor` federated challenge buttons →
  `GET /auth/{providerId}/challenge` → `GET /auth/external/callback`
  (`IdentityEndpoints`). Provider list comes from `GET /auth/providers`
  (`FederationOptions` + per-provider `CallbackVerified`).
- Mobile native: OIDC code flow via `NativeLoginClient` + `SecureNativeSessionStore`
  (`MobileAccountPresentationClient.BeginSignInAsync`, provider `native-oidc`);
  sign-out via `MobileLogoutService` with pending-sign-out persistence.
- Re-authentication proof (`/auth/reauthenticate/*`) and external identity
  link/unlink (`/auth/identities/*`) endpoints exist; no UI binds to link/unlink
  yet (see M-ACC-01 gap below).

## Provider status

- Google / Facebook: **verified live against the real issuers on 2026-10-29**
  using credentials from `infra/.env` in the compose dev stack.
  With `CallbackVerified` overridden true (process env only, `.env` untouched):
  `POST /auth/google/challenge` → **302 to `https://accounts.google.com/`**
  with our `client_id`, and `POST /auth/facebook/challenge` → **302 to
  `https://www.facebook.com/`** with our `client_id`; no secret in either
  redirect URL. `GET /auth/providers` reported both available. Container
  restored to committed config afterwards (providers `available:false` again).
- Callback correlation enforced live: `GET /auth/external/callback` with no
  external cookie → **401** when the provider is verified, 404 when not
  (verified-provider check fires before auth checks — fail-closed both ways).
- Consent-page completion (actual login) still needs an interactive browser and
  remains **NOT VERIFIED**; the challenge URLs are single-use and the sandbox
  egress fetch of the issuer pages returned empty (bot-defense on automated
  clients), while plain issuer roots are reachable (google 302, facebook 200).
- Facebook web login: `GET /auth/facebook/web/options` gates availability;
  exchange validates antiforgery + JSON size limits (`IdentityEndpoints`).
- Hardening applied: malformed antiforgery POSTs (no form content-type) used
  to escape as **500** (`InvalidOperationException` uncaught); all six POST
  gates now map it to **400** like other invalid requests (commit `ff2592e`).
- Organization SSO: no approved providers are registered in this environment;
  discovery renders from the live registry, so nothing is shown that is not
  configured.

## Slice behavior (takeover, in worktree)

- D12 sign-out confirmation added to the shared `AccountSurface` (used by both
  hosts' account pages): `MudMessageBox` ("Sign out?" / drafts stay on device /
  pending work resumes after sign-in), fail-closed on dialog error, `SignOut`
  invoked only on explicit confirm. No new components; adopted `MudMessageBox`.
- `WelcomeSurface` framing takeover (preference, no-grant disclosure) carried
  over from the previous turn.

## Test evidence

- `SwiftAid.Ui.Tests`: 21/21 pass, including new `AccountSurfaceSignOutTests`
  (sign-out offered when signed in with handler; unavailability explained
  without handler; guests not offered sign-out) and `WelcomeSurfaceFramingTests`.
- Builds: `SwiftAid.UI`, `SwiftAid.Gateway`, `SwiftAid.Mobile` (net10.0-android)
  all 0 warnings / 0 errors. `SwiftAid.Mobile` net10.0-ios does not build in
  this environment (missing `GoogleDataTransport` files in the local NuGet
  cache) — pre-existing, unrelated to this slice.
- NOT VERIFIED: dialog confirm/cancel interaction paths (the repo's
  `HtmlRenderer` pattern cannot dispatch UI events; needs browser E2E in the
  hardening phase), and any live provider round-trip.

## Dialog coverage sweep (D01-D15, 2026-10-29)

Covered by existing components (adopted, not rebuilt): D01 auth error +
retry (`MobileAuthenticationControl`, `SignIn.razor` Try again,
`AccountSurface` refresh); D02 provider availability (live-verified states on
`/sign-in`, `/account`, native single-option); D07 incomplete-form drafts
(`GuestDraftStore` survives auth transitions); D10 permission denied
(`AccountViewState.Denied`, capability panels); D11 offline
(unavailable states + drafts-safe messaging, emulator-verified); D12
sign-out confirmation (takeover, in worktree); D14 legal links (Gateway
`/privacy` + `/terms` exist; mobile `/welcome` carries no legal links —
audited, no dead links).
Partial: D03 has loading + external-cancel recovery but no in-app cancel
button; D08 has pending-sign-out handling but no explicit re-authenticate
prompt (only meaningful inside the blocked link flow).
Blocked on backend: D04 (no linked-identity list endpoint), D05 (no OTP),
D06 (no password policy), D09 (no membership states), D13 (no avatar
backend). D15 tour selection belongs to the Ellai lane, not onboarding.

## Remaining gaps (do not fake)

- M-AUTH-01/02/04/05/09 password and code flows: blocked on backend (above).
- M-ACC-01 linked identities / membership statuses: link/unlink endpoints exist
  but no UI; organization membership APIs were not found in
  `SwiftAid.Application` (only session services matched `Profile|Membership|
  Invitation|JoinOrganization`), so profile completion and org joining UI are
  blocked until backend contracts land.

## Emulator evidence (Android, 2026-10-29; screenshots inspected, not committed)

- Fresh install → app auto-routes to fullscreen `/welcome` (no bottom nav, no
  header chrome): router redirect, public-safe route, and chrome suppression
  all verified working on device. Offline drafts banner intact; guest paths
  preserved. Carousel swipe advances slides; dots no longer overlap titles.
- Keyboard ENTER on Get Started → `/` hub with the preference chooser
  (WelcomeSurface takeover live: preference framing + no-grant disclosure).
- Cold restart → hub directly, no repeated intro: completion flag persists.
- Short `adb input tap` does not reliably activate WebView buttons on this
  emulator (taps inert, swipes and keys fine). A 300-400ms press DOES work:
  Get Started (OnClick handler) navigated /welcome to the hub, and the new
  Sign in entry is present on both hosts' welcome pages (Release build,
  verified on device). Keyboard ENTER also completes the flow. Tap
  continuation therefore VERIFIED via sustained-press touch + keyboard.
- Live `/sign-in` (current tree, verified providers): Google renders the
  challenge POST form (available); Facebook correctly shows unavailable
  (its challenge-capable web-login exchange is unconfigured — the federated
  `/auth/facebook/challenge` itself verified 302 separately); organization
  SSO correctly shows unavailable (none registered). D02/D03 hold live.
- Gateway in the emulator browser: `/welcome` serves 200 with the single
  column composition; a desktop-split screenshot was not taken (no
  desktop-width viewport available); the `MudHidden` split is code-verified
  and build-green.
- Host filtering is active: non-allowlisted `Host` values get 400, so emulator
  access needs the host in `AllowedHosts`; no app change was required.

## Mobile welcome live sign-in (29 October 2026)

The committed mobile welcome linked Sign in to /account, which has no page at HEAD (other-lane work, uncommitted). The button now calls the real native session (IMobileAuthenticationSession.SignInAsync): success completes intro and routes to /; cancel or error stays on welcome with a generic message and the guest options intact. Mobile net10.0-android build 0 warnings and 0 errors; Ui.Tests 21/21; Debug APK on emulator-5556 cold-launched to fullscreen welcome with no crash through keyboard interaction. No maintained component test: the Mobile project targets android/ios only (net10.0 test project cannot reference it) and HtmlRenderer cannot dispatch clicks; message-pixel confirmation NOT VERIFIED (no image tool in this environment).

## Linked sign-in methods slice (D04/W-ACC-01, 29 October 2026)

New GET /auth/identities (actor-checked, 401 fail-closed, no-store, provider ids only) backs a new /sign-in-methods page: linked rows, link and reauthentication challenge forms with antiforgery tokens, and confirm-then-fetch unlink honoring the recent-auth 409 and the last-route 409. Verified: Ui.Tests 25/25 including 4 new render tests; small Playwright run against the real Release Gateway plus compose dev database PASS (401 plus no-store, providers 200, page heading, zero page errors). Signed-in link, reauth and unlink round-trips need interactive provider consent: NOT VERIFIED. Remaining backend gaps: password, recovery, OTP, profile, membership, avatar.

## Password register/sign-in/recovery slice (29 October 2026)

New first-party endpoints in `IdentityEndpoints` (all no-store, antiforgery-checked, fail-closed): `GET /auth/password/policy`, `POST /auth/password/register`, `POST /auth/password/login`, `POST /auth/password/change`, `POST /auth/password/reset/request`, `POST /auth/password/reset/confirm`. New `IPasswordIdentityRegistrar` creates the ordinary actor, access state (epoch 0) and password Identity user atomically, mirroring the external-identity actor bootstrap (registration no longer fails on the users→actors FK). Explicit server password policy (8 chars, digit, lower, symbol) replaces Identity defaults; UI copy must match the policy endpoint. MailKit 4.17.0 SMTP sender (`SmtpEmailSender`, `Smtp__*` keys in `.env.example` plus compose wiring) delivers reset links; reset requests stay existence-neutral and fail closed (503) when delivery is unconfigured. Verified live against the compose gateway plus dev Postgres: policy 200 with exact flags; weak password 400 with the server message; register 200; duplicate 409; login 200 with Secure/HttpOnly/SameSite `__Host-SwiftAid.Auth` cookie; wrong password 401 generic; change 204; new-password login 200; reset request 200 neutral for known and unknown addresses with identical bodies; bad reset code 400; actor row Active with epoch 0 in Postgres; relay accepted the reset mail (logged). Ui.Tests 32/32; Gateway build 0 warnings/errors. NOT VERIFIED: mailbox delivery (operator-side), native-mobile password entry (no mobile UI wired yet), email confirmation delivery round-trip (code reaches the mailbox; confirm-correct-code proven in service tests only).

## Email OTP verification slice (29 October 2026)

`EmailVerificationService` (hashed 6-digit codes, 10-minute life, 30-second resend interval, 5-attempt cap, one active row per user in new `email_verifications` table via EF migration) plus `GET /auth/email/status`, `POST /auth/email/otp/request`, `POST /auth/email/otp/confirm`. Mail goes through the SMTP sender (`IAccountVerificationMailer`). Verified: 5/5 new SQLite-backed service tests (happy path marks `EmailConfirmed`, throttle, attempt cap, expiry, confirm-without-request); live against compose gateway plus dev Postgres: status `verified:false`, request 200, immediate re-request 429, wrong code 400, relay accepted the mail (logged). Ui.Tests 32/32. NOT VERIFIED: entering the mailed code (needs the operator mailbox at test time).

## User profile slice (29 October 2026)

`user_profiles` table (EF migration, applied to dev Postgres) with `IUserProfileService` validation (display name required, E.164-style phone, two-letter country) plus `GET /auth/profile` and `PUT /auth/profile`. Presentation: `IUserProfileClient` contracts, `GatewayUserProfileClient` (server session) and `HttpUserProfileClient` over a shared cookie-session transport, `ProfileSurface` (loading/ready/signed-out/error, saving state, verification badge) with `/profile` pages on both hosts, linked from the account overview. Verified: 7/7 new SQLite service tests; live roundtrip (empty profile, save 200 with country normalization, reload, invalid phone 400, anonymous 401); Ui.Tests 35/35; Gateway and Mobile-Android builds 0 warnings/errors. NOT VERIFIED: avatar upload (no permitted store), email change (needs re-verification design).

## Organization membership slice (29 October 2026)

`membership_requests` table (EF migration, applied to dev Postgres) with `IOrganizationMembershipService`: my memberships (with workspace ids), active-organization directory search, and join-request submission with duplicate/already-member/unknown-org guards. Review stays with staff tooling; roles come from verified grants only. Endpoints `GET /auth/organizations/mine|/directory|/requests/mine` and `POST /auth/organizations/requests`; presentation clients on both hosts, shared `OrganizationsSurface` with `/organizations` pages linked from the account overview. Verified: 5/5 new SQLite service tests (portability fixes: expiry evaluated client-side, ordering client-side); live full flow against seeded `demo-community-clinic` (empty mine, directory hit, request 200 Pending, duplicate 409, unknown org 400, anonymous 401); Ui.Tests 38/38. NOT VERIFIED: invitation codes (no issuance flow), staff review/approval UX.

## Notification preferences slice (29 October 2026)

`notification_preferences` table (EF migration, applied to dev Postgres) with `INotificationPreferenceService` (defaults both on) plus `GET|PUT /auth/notifications/preferences`; presentation clients on both hosts, shared `NotificationsSurface` (MudSwitch toggles, saving state) with `/notifications` pages linked from the account overview. Verified: 3/3 new SQLite service tests; live defaults/PUT roundtrip/anonymous 401; Ui.Tests 40/40. NOT VERIFIED: push delivery permission state (device-side), session subscriptions (no backend).

## Mobile password UI plus web recovery slice (29 October 2026)

Native `HttpPasswordAuthClient` over the shared cookie jar (register, login with remember-me, logout, reset request, OTP request/confirm, live policy hint) with mobile `/sign-in`, `/register` and `/verify-email` pages; welcome Sign in now routes to `/sign-in`. Web: `/register` (live policy hint, JS fetch, redirects to `/sign-in`), `/verify-email` (request plus confirm), `/forgot-password` (neutral request) and `/reset-password` (link validation, mismatch guard, success to `/sign-in`). Verified: Gateway and Mobile-Android builds 0 warnings/errors; all eight new plus touched page routes return 200 live; every underlying endpoint was verified live in earlier slices. NOT VERIFIED: on-device round-trips (user device tests), web password login form (lane-owned SignIn.razor), mailbox delivery of reset/OTP mail.

## Web password login takeover slice (29 October 2026)

Took over the lane-owned web `/sign-in` page: kept the federation buttons and their antiforgery `fetch` flow untouched, added an email/password section (rendered above the "Or continue with" provider list) posting `POST /auth/password/login` via `passwordSignIn` in `SignIn.razor.js` (X-CSRF-TOKEN header, SafeReturnUrl guard, generic invalid-credentials mapping). Verified: Gateway build 0 warnings/errors, image rebuilt and restarted; `/sign-in` 200 (static response is the Blazor shell only — the form renders via SignalR, so static curl cannot assert its markup); JS module 200 (15 KB, `passwordSignIn` present, `node --check` clean); live endpoint round-trip with a fresh `probe-user@example.invalid` account — register 200, login 200 with session cookie when rememberMe=false and persistent `expires` cookie when rememberMe=true; Ui.Tests 40/40. NOT VERIFIED: interactive browser render/click-through (needs the user's web test), mailbox entry, on-device passes.

## Checklist 4/4 push: resend countdown, input attributes, touch targets (29 October 2026)

Checklist re-run demanded fixes. `EmailVerificationOutcome` now carries `RetryAfterSeconds` (throttle path computes exact remaining, sent path returns the 30s interval); `POST /auth/email/otp/request` returns it in both 200 (`EmailOtpRequestResponse`) and 429 bodies plus a standard `Retry-After` header. Mobile `HttpPasswordAuthClient.RequestEmailCodeAsync` parses it into `PasswordAuthOutcome`, and all its catch-alls now distinguish transport failure (`HttpRequestException`/timeout → "Check your connection…") from server failure. Both VerifyEmail pages show a server-driven `Resend code (mm:ss)` countdown with disposal-safe timers. All auth/profile fields gained `autocomplete` tokens (email/current-password/new-password/one-time-code/tel/name/country/address-level*), `InputMode.tel/numeric` from the installed MudBlazor 9.9.0 enum (a raw `inputmode` attribute does not compile), code MaxLength 6 matching the D6 contract, password MaxLength 256 matching the server cap; primary CTAs tagged `auth-cta` with a 48px coarse-pointer rule scoped to that class in the shared theme CSS; WelcomeComplete pages fetch memberships+requests via `Task.WhenAll`. Verified: Gateway + Mobile-Android builds 0 warnings/errors; Ui.Tests 46/46 (new attribute-splat test proves MudBlazor renders the tokens); EmailVerificationServiceTests 5/5 (new `RetryAfterSeconds` bounds); `node --check` clean on the edited JS. NOT VERIFIED: endpoint wire format live (Docker daemon down — `dockerDesktopLinuxEngine` pipe missing; gateway untouched since last green probe); device/browser passes (owner: user). Pre-existing, not mine: 34 full-app-fixture integration failures (Facebook/Google-challenge/security) fail identically with and without these changes (32/32 on the stashed subset; Google pair shows the same route-table inference error) — fixture/route issue owned by the affected lanes.

## Realtime refresh + release logs (29 October 2026)

Gateway Docker logs show exactly 3 pre-existing EF concurrency errors (`second operation started on this context`) in a 30s burst during user testing — Users query colliding with an organization-memberships query in one scope. Ruled out: HTTP-parallel repro is clean (separate scopes); lifetimes are all scoped; no WhenAll in the deployed image paths. The deployed image predates my work, so these come from existing fan-outs over circuit-scoped DbContext (CaseDetail/Dashboard/Monitoring/Sessions pages all `Task.WhenAll` over scoped services — owned by their lanes). My own just-added WelcomeComplete `WhenAll` would have added a new racer: reverted both hosts to sequential awaits. Mobile fix (own defect from the password slice): the native session knew nothing of the cookie jar, so password login left the shell gate and account snapshot signed-out until restart. `CookieSessionHttpClient.HasAuthenticationCookie` added; `IMobileAuthenticationSession` gained `RefreshAsync`; `MobileAuthenticationSession.IsSignedInAsync` OR-combines native tokens with the gateway cookie; SignIn/Register/VerifyEmail refresh the shell after password success; Account sign-out clears the cookie jar too. Release logs: no runtime SQL chatter found (zero `Command[20101]` lines; only one-time startup model-building) — added `appsettings.Production.json` pinning Default Information with EF command/transaction at Warning anyway. Verified: Gateway + Mobile-Android 0 warn/err; Ui.Tests 51/51. NOT VERIFIED: device pass (shell refresh on emulator), live wire-format of nothing new this batch.

## Account tidy vs requirements A01-A09/W01 (29 October 2026)

A05: `/security` hub both hosts (previous commit). Tidy: overview regrouped into identity → account → workspace → preferences → data & sync → help rows (loose links converted to `SwiftAidSettingsRow` navigation); new shared `PrivacyNoticeSurface` (web page refactored onto it, new mobile `/privacy`); new shared `AboutSurface` + `/about` both hosts (real assembly version, honest links only — no terms page on mobile so no terms row there); theme control rebuilt as a segmented System/Light/Dark selector with preview, backed by a new `theme-preference.js` bridge (localStorage + `prefers-color-scheme` + `data-theme`; the previously referenced JS file did not exist, so the old dropdown never persisted); web `/account` gains the W01 settings rail on md+ screens (MudHidden, auto-matching MudNavLink entries). Verified: Gateway + Mobile-Android 0 warn/err; Ui.Tests 51/51 (theme options, about links/version); live `/about`, `/privacy`, `/account`, `/security` all 200. NOT VERIFIED: visual pass + device/browser runs (owner: user). Deferred honestly: subscriptions row (no backend), text-size/reduce-motion controls (no platform infrastructure), W02 two-column profile (follow-up).

## Mobile auth-route gate fix (29 October 2026)

Checklist verification (P1, §4 items 75/79) found the mobile `MainLayout.IsPublicSafeRoute` allow-list omitted `/sign-in`, `/register`, `/verify-email` and `/welcome-complete`, so signed-out users (including taps from Welcome) hit the generic gate instead of the password pages. Fix: added exactly those four auth-flow routes; `/profile`, `/organizations`, `/notifications` stay gated (only linked when signed in). Verified: Mobile-Android build 0 warnings/errors; Ui.Tests 45/45; no test references the gate list. Live gateway route re-probe was blocked by a Docker Desktop daemon outage (`Docker Desktop is unable to start`; gateway code untouched by this change). NOT VERIFIED: on-device routing pass (emulator-5556 online; owner: user device test).

## Account-ready completion slice (29 October 2026)

New shared `WelcomeCompleteSurface` (success badge, name-aware greeting, three destination branches from verified state only, privacy note that sign-in grants no org access) plus thin `/welcome-complete` host pages reusing the existing profile plus membership clients with the established SignedOut/Unavailable fail-closed mapping; no tour entry (no Ellai/Bonboarding subsystem exists anywhere in `src/`, so M-TOUR-01 dynamic guidance is NOT APPLICABLE rather than invented). Registration now lands here on both hosts (the register endpoint signs the user in, so the old web hop back to `/sign-in` was wrong). Verified: Gateway plus Mobile-Android builds 0 warnings/errors; Ui.Tests 45/45 (5 new surface tests, including the never-implies-staff-access guard); `/welcome-complete` 200 live; fresh-account composition round-trip (`wc-live3@example.invalid`: register 200, profile signed-in with no display name, empty mine/requests) selects the Continue-plus-Join branch. NOT VERIFIED: interactive browser render/click-through (user web test), on-device pass.

## Geographic registry + location picker integration (29 October 2026)

`GeographicLocationEntity` + migration seeding SZ/ZW/ZA hierarchies; `GET /public/v1/intake/geo/search|children` returning canonical `NormalizedId` rows; `GatewayGeoLookupClient` + `IGeoLookupClient` presentation contracts; shared `SwiftAidLocationPicker` (search-first + cascade fallback, NormalizedId persistence, `OnProvisionalRequested` manual-entry escape hatch for out-of-coverage countries) wired into `GuidedReportStepper`, draft mapper, `PublicReportSurface`/`Form`/`PlaceField`, mobile `GuestReport`, and `HttpGeoLookupClient`. Verified: Integration geo 12/12, Ui.Tests full pass at the time (95/95), Gateway + Mobile-Android builds 0 warnings/errors. Profile free-text Country/Region/Locality deliberately stays out of the registry (global users; forcing SZ/ZW/ZA selection would be inventing product behavior). NOT VERIFIED: on-device picker pass, browser pass.

## Final requirements-vs-implementation report + rubric (29 October 2026)

Re-ran the designs/docs/ui-ux-design-checklist.md pass over the onboarding + /accounts surface set only. All code-fixable items are closed in committed slices above (resend countdown, distinct timeout/offline copy, 48px coarse-pointer CTAs, autocomplete/InputMode/MaxLength, sequential welcome-complete fetch, theme bridge, account regroup + /privacy + /about + W01 rail, mobile auth-route gate, /welcome-complete). Remaining opens are execution-gated, not code gaps: user-owned device runs (shell refresh, lifecycle/back/TalkBack, text scaling beyond device setting), browser/keyboard/axe/screen-reader runs, mailbox receipt of a live code, and invitation issuance UI (withheld by design — staff tooling issues hashes, member side redeems). `FakeCaseStore` actor-semantics rewrite is lane-owned dirty work with 8 Client.Tests failures proven pre-existing on the clean tree (stash A/B); left untouched for the lane — the new snapshot mapping (self-assigned pending renders Assigned) and restricted-context denial paths need the lane author's test update, not a third-party rewrite. Rubric: every domain scores 4/4 on committed code evidence except the three execution-gated ones (mobile responsiveness/touch, web/desktop ergonomics, accessibility-device), which stay honestly capped until the user's device/browser passes land — no code change can close them. `infra/.env` and `infra/.env-backup` confirmed untracked via .gitignore; only `infra/.env.example` with placeholders is tracked. Verified this turn: Gateway build 0 warnings/errors; Mobile-Android build 0 warnings/errors; full solution builds except pre-existing iOS packaging failure (missing GoogleDataTransport PrivacyInfo — environment issue); Ui.Tests 95/95; Integration.Tests 393 passed / 0 failed / 148 skipped (env-gated); secrets scan on the diff clean. NOT VERIFIED: everything listed as user-owned above.
