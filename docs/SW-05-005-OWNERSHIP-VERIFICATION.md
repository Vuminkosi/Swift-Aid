# SW-05-005 ownership verification

Status: PARTIAL. Source and PostgreSQL verification on interactive-ellai.

The domain requires a protected reassignment request and approved review, with reasons and an eligible target. The accepted assignment/review event pair updates the accountable owner. CaseCommandProcessingPostgresTests.Approved_assignment_requires_prospective_target_scope_and_increments_both_owner_epochs passed against a disposable PostgreSQL database, verifying owner projection, aggregate/resource versions, two event IDs, and increments to both owners' access epochs.

CaseAuthorityPostgresTests passed 16/16, zero skips. Coverage includes locked-case scope, active-share boundaries, current epoch/status/expiry denial, membership/workspace state, parallel reads, serialized writes, write rejection during read leases, rollback, owner-discovery changes, and cancellation.

Environment: task-created swiftaid-assignment-verification using postgis/postgis:17-3.6-alpine, tmpfs storage, loopback port 32703, standard infra/testing/postgres-init.sql. Gateway --migrate-database applied 46 migrations to swiftaid_p21a. Set SWIFTAID_RUN_SQLAUTH_FIXTURES=1 and SWIFTAID_TEST_POSTGRES_CONNECTION to that disposable database using the repository fixture role.

Commands:

- `dotnet src/SwiftAid.Gateway/bin/Debug/net10.0/SwiftAid.Gateway.dll --migrate-database` with ConnectionStrings__SwiftAid set to the disposable database: PASS, 46 migrations.
- `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter FullyQualifiedName~Approved_assignment_requires_prospective_target_scope --verbosity normal`: PASS 1/1.
- Same test invocation with `--filter FullyQualifiedName~CaseAuthorityPostgresTests --verbosity quiet`: PASS 16/16.

Remaining requirements: CommandBackedWorkManagementClient explicitly sets CanAcknowledgeAssignment=false. No completed assignment acknowledgement workflow is claimed. Bounded collaborator management, new-owner receipt of older cases, prior-owner removal from sync/media, cached reconciliation, and preservation of open assistance/follow-ups across transfer require their own evidence. Browser/Android ownership journeys are NOT VERIFIED. The tests above do not establish full SA-ASG-01 through SA-ASG-07 acceptance.

SW-05-005 acknowledgement domain slice: added AcknowledgeAssignmentCommand, dedicated capability flag, CaseAssignmentAcknowledged event, current AssignmentVersion and AssignmentAcknowledgedAtUtc. Current owner plus explicit capability required; stale assignment version, duplicate acknowledgement, resolved workflow and forged foreign-owner replay rejected. Initial assignment, reassignment and reopening establish/reset assignment acknowledgement state. Tests cover replay, unchanged clinical/workflow state, owner/capability/version denial and reopening even to same owner. PASS 86/86: dotnet test tests/SwiftAid.Domain.Tests/SwiftAid.Domain.Tests.csproj --no-restore -m:1 -nr:false --verbosity quiet. PASS 15/15: dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~CaseCommandProcessorTests --verbosity quiet. Domain-only slice: serializer, database event constraint/projection, operation mapping, API and UI remain unconnected. CanAcknowledgeAssignment stays false. Do not claim durable acknowledgement or checklist completion until those paths and PostgreSQL/browser/device checks pass.
