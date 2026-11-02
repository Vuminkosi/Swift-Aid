# SW-05-001 managed case event and command audit - 2 November 2026

## Outcome

Domain/application source and focused tests cover typed case commands, event serialization, deterministic replay/order validation, report correction revisions, unknown/presence semantics, expected-version checks and idempotent command handling. This is partial because the PostgreSQL atomic transaction spanning event journal, projection, receipt, audit and outbox is covered by a disposable-database-gated suite that was not run in this audit.

## Verification

- `dotnet test tests/SwiftAid.Domain.Tests/SwiftAid.Domain.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~CaseAggregateTests --verbosity minimal` — **PASS**, exit 0, 20 passed, 0 failed/skipped.
- `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~CaseCommandProcessorTests --verbosity minimal` — **PASS**, exit 0, 15 passed, 0 failed/skipped.

Sources include `src/SwiftAid.Domain/Cases/CaseAggregate.cs`, `src/SwiftAid.Application/Cases/Commands/CaseCommandProcessor.cs`, `src/SwiftAid.Application/Serialization/CaseEventSerializer.cs`, and `src/SwiftAid.Infrastructure/Persistence/Processing/PostgresCaseCommandPersistence.cs`.

## Limits

`tests/SwiftAid.Integration.Tests/CaseCommandProcessingPostgresTests.cs` covers atomic persistence, audit/outbox, scoped access epoch updates, rollback and malformed/replay payloads, but is PostgreSQL-gated. No live database test, projection rebuild/replay from a real journal, or full case workflow was run for this audit.
