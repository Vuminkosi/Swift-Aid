# SW-02-010 onboarding and account UX audit - 2 November 2026

## Outcome

Source inventory found Welcome, SignIn, Profile, Account and WelcomeComplete surfaces on Mobile, Gateway account/welcome pages, profile/access clients, and persistent local Mobile welcome/introduction preference stores. UI test sources cover profile, account sign-out and public auth shell. No rendered responsive/accessibility review or end-to-end server-backed first-use journey was performed for this audit. Keep SW-02-010 partial.

## Evidence and limits

- Mobile pages: `src/SwiftAid.Mobile/Components/Pages/Welcome.razor`, `WelcomeComplete.razor`, `SignIn.razor`, `Profile.razor`, `Account.razor`; local completion/preference services are under `src/SwiftAid.Mobile/Services/Welcome/`.
- Gateway has account/welcome pages and `GatewayUserProfileClient` / `GatewayAccountAccessClients`; this indicates server-backed client surfaces are wired in source but does not prove successful persistent profile or onboarding state.
- UI tests include `ProfileSurfaceTests`, `GatewayAccountPageTests`, `AccountSurfaceSignOutTests` and `PublicLayoutAuthShellTests`. These are source inventory only; no test was run here.
- Browser Playwright is unavailable under BL-003; no browser screenshot, compact viewport, keyboard, screen reader or Android-device evidence was captured for the onboarding/account journey.

## Closure evidence required

Verify guest-first entry and return paths, registration/sign-in/profile completion, local/server state restoration, logout/account switch and first-use guide entry on web and Android. Review compact mobile/tablet/desktop layouts, keyboard and screen-reader semantics, focus restoration, validation/error/empty/loading states and reduced motion. Confirm onboarding state is accurately sourced from server versus local preference. No source or test was changed or run for this audit.
