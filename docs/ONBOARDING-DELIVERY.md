# Onboarding delivery report — Phases 0–3 (29 October 2026, UTC)

Execution rule for this subsystem: no fake clients, no mock success, no parallel
design system. Where the backend has no contract, the UI is absent and the item
is marked Blocked — never simulated.

## Completed surfaces and route paths

| ID | Surface | Route(s) | Status |
|---|---|---|---|
| M-ONB-01–03 | Brand welcome + Manage Cases + Educate Communities + Report-concern intro | `/welcome` on Mobile (MAUI Hybrid) and Gateway (InteractiveServer) | Done, committed |
| M-ONB-04 / W-PUB-01 | Public entry hub (guest report, tracking, sessions, account) | `/` via existing `PublicHomeSurface` on both hosts | Pre-existing, preserved untouched |
| M-AUTH-01/02/04/05/09, W-AUTH-03, M-PRF, M-DONE | Password sign-in/create/recovery, OTP, profile, completion | — | **Blocked**: no backend (see below) |
| D12 / M-ACC-01 | Sign-out confirmation | Shared `AccountSurface` (both hosts' `/account`) | Done, uncommitted (takeover, see below) |
| D02/D03/D11 | Provider availability, loading/cancel, offline states | Existing `SignIn.razor`, `MobileAuthenticationControl`, `AccountSurface` | Pre-existing, preserved |

## Reused components and justified additions

Reused only: `SwiftAidPageHeading`, `WelcomeSurface`, `PublicHomeSurface`,
`AccountSurface`, `SwiftAidStatePanel`, `ThemePreferenceControl`, MudBlazor
`MudCarousel`/`MudButton`/`MudIcon`/`MudMessageBox`/`MudContainer`,
`swiftaid-icon` asset. A first attempt at new `Onboarding/*` primitives was
reverted (`1ccd205`) per the adopt-don't-invent correction. Added files are
pages/tests/docs only — zero new shared components. No new CSS framework,
package, auth system, navigation system, or identity client.

## Backend contracts integrated (all real, inspected before use)

- `POST /auth/{providerId}/challenge` → `GET /auth/external/callback`
  (`IdentityEndpoints` + `SignInManager` external login + `IExternalIdentityResolver`).
- `GET /auth/providers` (`FederationOptions` + per-provider `CallbackVerified`).
- Reauth (`/auth/reauthenticate/*`, 5-minute window) and identity link/unlink
  (`/auth/identities/*`) endpoints exist; link/unlink has **no list endpoint**,
  so linked-identity management UI is Blocked.
- Native OIDC code flow (`NativeLoginClient`, `SecureNativeSessionStore`,
  `MobileAccountPresentationClient`); Gateway cookie identity; OpenIddict
  code+refresh issuer (`IdentitySetup`).
- Guest intake stays on the real offline path (`GuestDraftStore`,
  `GuestSubmissionCoordinator`); onboarding never touches it.

## Authentication/provider capability status

- Federated sign-in (Google/Facebook/org SSO): UI wired to real endpoints.
  Live credential provisioning per environment: **Blocked / Not provisioned**.
- Password registration/sign-in, reset, email confirmation, OTP verification,
  profile persistence, organization join/request/approval: **Blocked — no
  backend contract exists** (exact negative probes in
  `docs/ONBOARDING-AUTH-EVIDENCE.md`). No fake forms were built.
- Professional role/participation selection grants nothing; only verified
  membership confers authority (enforced by backend, disclosed in UI copy).

## Tests executed (environments and exact results)

- `SwiftAid.Ui.Tests`: **21/21 pass** (.NET 10) — 16 pre-existing plus
  `WelcomeSurfaceFramingTests` (preference framing, no-grant disclosure) and
  `AccountSurfaceSignOutTests` (sign-out offered/explained/guest-excluded).
- `SwiftAid.Client.Tests`: 272 pass, **8 fail — pre-existing, not regressions**.
  All 8 are `FakeWorkManagementClientTests` /
  `WorkManagementAndIntakeReviewContractTests`; every production file in that
  area is another stream's uncommitted M/?? work, and `Client.Tests` references
  only `SwiftAid.Client` (none of this slice's files). Left untouched.
- Builds: `SwiftAid.UI`, `SwiftAid.Gateway`, `SwiftAid.Mobile`
  (net10.0-android) — 0 warnings / 0 errors. `SwiftAid.Mobile` net10.0-ios
  does not build here (missing `GoogleDataTransport` files in the local NuGet
  cache) — pre-existing environment issue.

## Android Release and web verification status

- No Android Release device run, no browser automation, no screenshots from
  this session: **NOT VERIFIED** (matches `implementation.md` NotRun ledger).
- Dialog confirm/cancel interaction paths: NOT VERIFIED in unit tests (repo's
  `HtmlRenderer` pattern cannot dispatch UI events) — owed to the hardening
  phase with browser E2E.

## Removed duplicate/dead code

- Reverted the parallel `Onboarding/*` primitive set in full. No other
  superseded code was found; no working route was deleted.

## Canonical commits and scope

- `939905b` → reverted by `1ccd205` (parallel primitives, superseded).
- `cbe2bfc` `feat(onboarding): compose welcome intro pages from existing components`
  (4 files: Mobile/Gateway `Welcome.razor` + `.css`).
- `55ab913` `docs(onboarding): record Phase 3 auth capability evidence`.
- This report + `docs/PROGRESS.md` entry (pending commit on top).

## Remaining blockers and unapplied changes

- Uncommitted takeovers (applied + tested in worktree, owned files belong to
  other uncommitted streams, so not committed): `WelcomeSurface.razor` framing
  edit, `AccountSurface.razor` D12 confirmation edit,
  `tests/.../WelcomeSurfaceFramingTests.cs`,
  `tests/.../AccountSurfaceSignOutTests.cs`.
- Blocked on backend: password auth, recovery, verification, profile,
  membership/org joining, linked-identity listing, completion routing by
  membership.
- Blocked on environment: live provider callbacks, Android device runs,
  browser E2E, screenshots.

## Requirement and acceptance mapping

- Preserved: AT-02 guest/staff separation (no verified identity ⇒ guest path
  only), AT-49 guest report without login (untouched real `/report` offline
  path), SA-PUB/SA-OFF guest isolation (drafts survive auth transitions per
  existing coordinator; surfaces restate device-locality).
- Honored: SA-IDN boundaries (no client-created authority; external challenge
  only; no provider secrets in UI); SA-ACL (preference ≠ permission, disclosed).
- NOT VERIFIED live: AT-57–65 (matches repo's own NotRun ledger — requires
  actual product evidence: devices, issuers, revocation, subscription races).
