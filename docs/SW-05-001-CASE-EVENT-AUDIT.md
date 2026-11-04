# SW-05-001 managed case event and command audit - 3 November 2026

## Outcome

Domain/application source and focused tests cover typed case commands, event serialization, deterministic replay/order validation, report correction revisions, unknown/presence semantics, expected-version checks and idempotent command handling. The disposable PostgreSQL command-processing suite has now verified atomic journal/projection/receipt/audit/outbox writes and replay from persisted protected events. The item remains partial because a general projection rebuild/recovery operation, append-only comment behavior, and a complete case workflow have not been verified.

## Verification

- `dotnet test tests/SwiftAid.Domain.Tests/SwiftAid.Domain.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~CaseAggregateTests --verbosity minimal` — **PASS**, exit 0, 20 passed, 0 failed/skipped.
- `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~CaseCommandProcessorTests --verbosity minimal` — **PASS**, exit 0, 15 passed, 0 failed/skipped.
- Disposable PostgreSQL 17/PostGIS 3.6 container `codex-swiftaid-case-command-audit` at loopback port 32703; EF migrations applied through `20261103180000_EscalationActionRecorded`; `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~CaseCommandProcessingPostgresTests --verbosity minimal` — **PASS**, exit 0, 15 passed, 0 failed/skipped. Includes atomic persistence and action-owner update followed by protected-journal replay.
- The owner-transfer test required the current migrations; the stale schema rejected `ActionOwnerChanged`. Removed the collaborator test's migration rollback probe because it downgraded event-type migrations while that test's collaborator events were present. Dedicated migration guard tests remain in place.

Sources include `src/SwiftAid.Domain/Cases/CaseAggregate.cs`, `src/SwiftAid.Application/Cases/Commands/CaseCommandProcessor.cs`, `src/SwiftAid.Application/Serialization/CaseEventSerializer.cs`, and `src/SwiftAid.Infrastructure/Persistence/Processing/PostgresCaseCommandPersistence.cs`.

## Limits

`tests/SwiftAid.Integration.Tests/CaseCommandProcessingPostgresTests.cs` covers atomic persistence, audit/outbox, scoped access epoch updates, rollback and malformed/replay payloads against disposable PostgreSQL. Persisted journal replay was verified for action-owner transfers. A general projection rebuild/recovery operation, append-only comment behavior, browser/device behavior, production database behavior, and the complete case workflow remain **NOT VERIFIED**.
