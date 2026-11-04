# SW-02-007 organization creator scope audit - 2 November 2026

## Outcome

Focused source and test review found organization creator access/backfill logic and a PostgreSQL-gated workspace-selection test that asserts another organization's workspace/destinations are not exposed. These are useful scoped controls, but SW-02-007 requires the full creator visibility and cross-organization escalation scenarios, including a live disposable database run; keep the item partial.

## Evidence

- `src/SwiftAid.Infrastructure/Identity/OrganizationMembershipService.cs` handles organization creation and membership lookup separately from a user-selected role.
- Migration `OrganizationCreatorAccess` backfills creator access under constraints; `tests/SwiftAid.Integration.Tests/GatewayWorkspaceAccessTests.cs` covers selection membership/session revalidation and asserts the second organization's workspace/destinations are absent from the first actor's view.
- That test uses the `IntakePostgresFact` gate and a PostgreSQL fixture. This audit did not run it; the prior full integration suite had provider/database-gated skips.

## Closure evidence required

Run creator onboarding in a disposable PostgreSQL environment and assert exact initial workspace visibility, no inherited or inferred clinical/operations authority, no cross-organization reads/counts/import destinations, and deny after membership revocation/suspension. Confirm browser/mobile surfaces cannot widen access. No database or membership was changed for this audit.
