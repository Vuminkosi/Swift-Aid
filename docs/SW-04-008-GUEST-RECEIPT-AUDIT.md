# SW-04-008 guest receipt and routing-state audit - 2 November 2026

## Outcome

Source and focused endpoint tests confirm anonymous intake routes use bounded request bodies; intake submissions require a scoped, expiring capability and 32-byte receipt-proof digest; receipt state is reporter-scoped and exposes `ReceivedUnverified` plus routing state, not staff assignment. Routing distinguishes `NeedsRouting`, `RoutedForReview` and `NoServiceCoverage`. This is not proof of PostgreSQL persistence or production receipt durability, so keep SW-04-008 partial.

## Verification

Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~GuestIntakeTests --verbosity minimal`

Result: **PASS**, exit 0, 16 passed, 0 failed, 0 skipped. This uses in-process test hosts/fakes; it is not a live database or production service test.

Source paths: `src/SwiftAid.Gateway/Endpoints/PublicIntakeEndpoints.cs`, `src/SwiftAid.Application/Intake/IntakeService.cs`, `src/SwiftAid.Contracts/Intake/IntakeSubmission.cs`. Tests assert anonymous bounded routes and proof-bound receipt behavior with unverified intake states.

## Limits

No live database transaction, durable proof lookup, deployment receipt flow or end-to-end public web/Android submission was run. Actual attachment originals, routing coverage and persistence after restart remain separate checks.
