# SW-02-005 membership and provisioning audit - 2 November 2026

## Outcome

Source inventory shows organization membership and workspace access are represented independently and read through an `IOrganizationMembershipService`; a join request returns pending/request states and does not itself create an active membership. Membership records carry status, workspace IDs, validity and expiry. This is partial evidence only: the full creator-onboarding journey and every jurisdiction/sector/sensitivity grant intersection are not established by this focused review.

## Evidence

- `src/SwiftAid.Infrastructure/Identity/OrganizationMembershipService.cs` separates membership reads, organization search/create and join-request submission from authorization grants.
- `src/SwiftAid.Client/Presentation/OrganizationMembershipContracts.cs` and `OrganizationManagementContracts.cs` expose membership/request views; requested membership is represented as a request state, not an active grant.
- `tests/SwiftAid.Integration.Tests/OrganizationMembershipServiceTests.cs` has seeded service tests for own membership/workspace visibility and pending membership request rules. No test was run for this audit.
- Authorization scope records are separate under `src/SwiftAid.Application/Authorization/` and `src/SwiftAid.Infrastructure/Authorization/`; the extent of intersected organization/workspace/jurisdiction/sector/sensitivity enforcement across all APIs is tracked by SW-02-006 and was not fully reviewed here.

## Closure evidence required

Verify with a fresh account that role preference or join request grants no access until authorized provisioning; verify the organization creator's exact initial visibility; exercise pending/invited/SSO-only and revoked states; and test policy intersections on API, sync, counts, media, AI tools, search, export, maps and deep links. Runtime journeys and full-surface authorization remain NOT VERIFIED.

No membership was created or changed and no tests were run for this source audit.
