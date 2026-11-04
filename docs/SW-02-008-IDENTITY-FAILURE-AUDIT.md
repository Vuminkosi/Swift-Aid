# SW-02-008 identity failure-path audit - 2 November 2026

## Outcome

Selected negative-path source and integration tests exist for disabled/unverified providers, reauthentication proof mismatch, external identity mapping and unavailable services. The checklist's full matrix—pending/invited/SSO-only membership, denied consent, callback replay, revocation/suspension and provider outage across web and mobile—was not fully proven or executed. Keep SW-02-008 partial.

## Evidence

- `tests/SwiftAid.Integration.Tests/IdentityEndpointSecurityTests.cs` covers unavailable integrations, rejects provider challenges when `CallbackVerified` is false, and rejects reauthentication callbacks lacking external proof or having a provider-scheme mismatch.
- `tests/SwiftAid.Integration.Tests/ExternalIdentityMappingPolicyTests.cs`, `FederationTests.cs` and `DisabledIdentityChallengeTests.cs` cover selected mapping and disabled/provider-denied cases.
- `src/SwiftAid.Gateway/Endpoints/IdentityEndpoints.cs` maps unavailable/dependency error responses and verifies external identity state/proof on sensitive routes.
- No test was run for this audit. Existing tests are not evidence of a real provider callback replay, live outage, account revocation/suspension, or Android behavior.

## Closure work

Run a synthetic identity matrix across web and Android for invited/pending/SSO-only membership, denied consent, callback replay/state mismatch, account revocation and suspension, password/provider outage, reset/recovery and session invalidation. Verify protected offline data and guest work remain isolated through account transitions. Use provider test tenants and safe identities; no real provider was contacted in this audit.
