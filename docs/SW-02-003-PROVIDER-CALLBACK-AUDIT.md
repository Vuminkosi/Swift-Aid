# SW-02-003 external identity provider audit - 2 November 2026

## Outcome

Provider integration contracts and explicit callback-verification gates exist in source. Real Google, Facebook and organization OIDC callback acceptance was not performed. Do not mark this requirement passed from endpoint presence or unit tests; keep it partial/blocked under provider readiness BL-004.

## Evidence

- `src/SwiftAid.Infrastructure/Configuration/FederationOptions.cs` models Google/Facebook enablement, client IDs/secrets and `CallbackVerified`; organization providers separately carry authority, issuer, tenant mapping and `CallbackVerified`.
- `src/SwiftAid.Gateway/Endpoints/IdentityEndpoints.cs` maps configured provider challenge/callback and identity link/re-authentication routes. Disabled integrations have unavailable behavior/capability reporting.
- `infra/compose.yml` reads provider configuration from environment variables. Secret values were not inspected; their presence and correctness are unknown.
- Existing `ExternalIdentityMappingPolicyTests` and `FacebookWebLoginExchangeTests` cover selected mapping and bounded exchange logic. They do not exercise real provider registration, redirect URI, consent, callback, token validation or live email/social account behavior.

## Acceptance still required

For each enabled provider, verify a real registered callback URI, issuer/tenant restrictions, PKCE/state/nonce and token validation, consent-denied and provider-outage handling, identity mapping by issuer+subject, unlink protections and correct local logout. Only set the provider's verification gate after that provider-specific callback run. Keep an allowlisted organization OIDC instance separate from generic provider support. No provider call was made for this audit.
