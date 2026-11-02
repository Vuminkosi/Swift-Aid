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
