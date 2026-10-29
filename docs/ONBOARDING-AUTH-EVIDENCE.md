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
