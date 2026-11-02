# SW-04-009 intake routing and promotion audit - 2 November 2026

## Outcome

Source and application tests show routing distinguishes no coverage/ambiguous/needs-routing from an explicitly eligible queue; promotion rechecks authorized workspace resolution and uses stable report/command IDs to append the unchanged guest evidence into an owned managed case. This is partial: focused tests use application stubs and do not prove live catchment data, concurrent reviewers or one PostgreSQL transaction across promotion and evidence linkage.

## Verification

Command: `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~IntakePromotionProcessorTests --verbosity minimal`

Result: **PASS**, exit 0, 19 passed, 0 failed, 0 skipped.

The source path includes `src/SwiftAid.Application/Intake/IntakeService.cs`, `IntakePromotionProcessor.cs`, `src/SwiftAid.Infrastructure/Authorization/PostgresIntakePromotionWorkspaceResolver.cs`, and Gateway intake decision/coordination endpoints. `IntakePromotionProcessorTests` cover stable IDs, replay, missing/terminal source and authorization/workspace decision cases using stubs.

## Limits

No live eligible-organization catchment query, authenticated review/promotion, atomic PostgreSQL promotion, competing-reviewer race or immutable original-evidence readback was run. No assignment or service outcome is implied by receipt or routing status.
