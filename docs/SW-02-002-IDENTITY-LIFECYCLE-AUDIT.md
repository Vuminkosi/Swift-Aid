# SW-02-002 identity lifecycle audit - 2 November 2026

## Outcome

Gateway source contains password, email verification, reset, profile, external identity and logout endpoints. Mobile source contains a native auth session, secure session store, cookie persistence and logout service. This source inventory does not establish that every lifecycle flow works end-to-end on both clients; configured real-provider callbacks, email delivery, account switching and expiry behavior remain NOT VERIFIED. SW-02-002 remains partial.

## Flow map

| Lifecycle | Source evidence | Limit |
|---|---|---|
| Register/password login/change/reset | `src/SwiftAid.Gateway/Endpoints/IdentityEndpoints.cs` maps `/auth/password/register`, `/login`, `/change`, `/reset/request` and `/reset/confirm`; client auth contracts are under `src/SwiftAid.Client/Presentation/AccountAccessContracts.cs`. | Endpoint presence is not a successful user journey or proof of mail delivery/reset-link operation. |
| Email verification | Gateway maps email status and OTP request/confirm endpoints. | No verification email/OTP provider delivery was exercised. |
| External login/link/logout | Gateway maps configured provider challenge/callback, reauthentication, identity link/unlink and logout routes. | Provider options and live callbacks are not verified; allowlisted organization OIDC is a separate checklist item. |
| Profile completion | Gateway exposes profile read/save through user-profile services and client profile contracts. | No authenticated profile journey was executed. |
| Web session | `src/SwiftAid.Infrastructure/Configuration/ServiceCollectionExtensions.cs` configures cookie expiry/sliding behavior and persistent Data Protection registration in Gateway. | Configuration source only; expiry, revocation and cross-restart behavior were not tested in this audit. |
| Native session/account switch | `src/SwiftAid.Mobile/Services/Authentication/` contains `MobileAuthenticationSession`, `SecureNativeSessionStore`, `SecureGatewayAuthCookiePersistence`, `MobileLogoutService` and unavailable fallback. | No Android device test established secure-store behavior, logout/account-switch cleanup or wrong-account protection. |

## Closure evidence required

Run registration → email verification → profile completion → logout → login and password reset on web and Android; verify session expiry/revocation, account switching and protected offline data isolation; test provider-denied/outage paths; and verify real configured callbacks and mail delivery. Use synthetic identities and do not treat route or build evidence as provider/device acceptance.

No identity mutation, provider call or test was performed for this source audit. Existing identity tests and prior suite results remain scoped evidence, not end-to-end lifecycle verification.
