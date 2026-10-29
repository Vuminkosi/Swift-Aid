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

- Google / Facebook / organization OIDC: configured-or-not is read live from
  `/auth/providers`; unavailable providers render disabled with explanation
  (D02, existing behavior in `SignIn.razor`/`AccountSurface`). Whether real
  credentials are provisioned and callbacks verified in each environment is
  **Blocked / Not provisioned** — pending a run against real issuers, which
  this environment cannot perform.
- Facebook web login: `GET /auth/facebook/web/options` gates availability;
  exchange validates antiforgery + JSON size limits (`IdentityEndpoints`).

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

## Remaining gaps (do not fake)

- M-AUTH-01/02/04/05/09 password and code flows: blocked on backend (above).
- M-ACC-01 linked identities / membership statuses: link/unlink endpoints exist
  but no UI; organization membership APIs were not found in
  `SwiftAid.Application` (only session services matched `Profile|Membership|
  Invitation|JoinOrganization`), so profile completion and org joining UI are
  blocked until backend contracts land.
