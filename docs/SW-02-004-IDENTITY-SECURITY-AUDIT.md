# SW-02-004 identity security source audit - 2 November 2026

## Outcome

Source-level identity controls are present: OpenIddict requires proof-key-for-code-exchange for authorization-code flow; native client uses Duende OIDC client; identity mapping is keyed by provider, issuer and subject; and Mobile stores the native session through MAUI SecureStorage. No real-provider callback or Android secure-storage/device test was run, so SW-02-004 remains partial pending runtime acceptance.

## Evidence

- `src/SwiftAid.Infrastructure/Identity/IdentitySetup.cs` enables authorization-code flow and requires PKCE; the registered native client descriptor includes the proof-key requirement.
- `src/SwiftAid.Client/Authentication/NativeLoginClient.cs` uses `Duende.IdentityModel.OidcClient`; host OIDC settings validate HTTPS authority and registered redirect URIs. Exact runtime negotiation and callback security remain unverified without a real callback.
- `src/SwiftAid.Infrastructure/Identity/ExternalIdentityResolver.cs` models validated `(Provider, Issuer, Subject)` identity. `EfExternalIdentityMappingStore.cs` queries mappings on provider, issuer and subject. Email is not the mapping key; existing tests cover selected mapping policy.
- `src/SwiftAid.Mobile/Services/Authentication/SecureNativeSessionStore.cs` persists native sessions through MAUI `SecureStorage`, validates token fields and expiry, and serializes access with a semaphore. Physical platform-store behavior is not proven by source.
- `src/SwiftAid.Mobile/Services/Authentication/MauiSystemBrowser.cs` delegates system-browser authentication to the MAUI WebAuthenticator and validates callback URI against configured login/logout redirects.

## Verification boundary

Run provider-backed authorization-code/PKCE, state/nonce, callback mismatch/replay, issuer/subject collision and account-switch scenarios with test identities; then verify SecureStorage and logout/revocation behavior on supported Android devices. No source mutation or test/provider/device operation was performed for this audit.
