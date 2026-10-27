using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

[Collection(CaseAuthorityPostgresCollection.Name)]
public sealed class CaseAuthorityUnitOfWorkRuntimePostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task Restricted_runtime_identity_reads_authority_and_commits_only_approved_case_state()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        await using var profile = await CaseAuthorityRuntimeProfile.CreateAsync(fixture).ConfigureAwait(false);
        await using var dbContext = fixture.CreateRuntimeContext(profile);
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var actor = fixture.CreateActor();

        await using (var lease = await unitOfWork.BeginCaseAsync(
                         actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false))
        {
            Assert.Equal(new[] { fixture.ActorId }, lease.LockedActorIds);
            var grant = await new PostgresCurrentAccessGrantStore(dbContext, unitOfWork)
                .GetCurrentGrantAsync(actor, CancellationToken.None).ConfigureAwait(false);
            var descriptor = await new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork)
                .GetCurrentDescriptorAsync(fixture.CaseId, CancellationToken.None).ConfigureAwait(false);

            Assert.NotNull(grant);
            Assert.Equal(fixture.AccessEpoch, grant!.AccessEpoch);
            Assert.NotNull(descriptor);
            Assert.Equal(fixture.CaseId, descriptor!.CaseId);
            await lease.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await using (var lease = await unitOfWork.BeginCaseAsync(
                         actor, fixture.CaseId, [], CaseLeaseMode.Write, CancellationToken.None).ConfigureAwait(false))
        {
            var state = await dbContext.ActorAccessStates.SingleAsync(
                entity => entity.ActorId == fixture.ActorId).ConfigureAwait(false);
            state.AccessEpoch++;
            state.Version++;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await lease.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await using var ownerContext = fixture.CreateDbContext();
        var persistedEpoch = await ownerContext.ActorAccessStates.AsNoTracking()
            .Where(entity => entity.ActorId == fixture.ActorId)
            .Select(entity => entity.AccessEpoch)
            .SingleAsync().ConfigureAwait(false);
        Assert.Equal(fixture.AccessEpoch + 1, persistedEpoch);
    }

    [CaseAuthorityPostgresFact]
    public async Task Discovery_retry_reports_sorted_hints_after_owner_changes_while_helper_waits()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        await using var profile = await CaseAuthorityRuntimeProfile.CreateAsync(fixture).ConfigureAwait(false);
        var actor = fixture.CreateActor();
        var ownerBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false };
        await using var blocker = new NpgsqlConnection(ownerBuilder.ConnectionString);
        await blocker.OpenAsync().ConfigureAwait(false);
        await using var blockerTransaction = await blocker.BeginTransactionAsync().ConfigureAwait(false);
        await using (var lockOrganization = new NpgsqlCommand(
                         "SELECT organization_id FROM public.organizations WHERE organization_id = @organization_id FOR UPDATE",
                         blocker,
                         blockerTransaction))
        {
            lockOrganization.Parameters.AddWithValue("organization_id", fixture.OrganizationId);
            _ = await lockOrganization.ExecuteScalarAsync().ConfigureAwait(false);
        }

        await using var runtimeContext = fixture.CreateRuntimeContext(profile);
        var unitOfWork = new PostgresCaseCommandUnitOfWork(runtimeContext);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var beginTask = unitOfWork.BeginCaseAsync(
            actor,
            fixture.CaseId,
            [fixture.OwnerActorId],
            CaseLeaseMode.Write,
            cancellation.Token).AsTask();

        var blockerCompleted = false;
        try
        {
            var blockedOnOrganization = await WaitForRuntimeLockWaitAsync(
                    fixture.ConnectionString, profile.RuntimeApplicationName, TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            if (!blockedOnOrganization)
            {
                await blockerTransaction.RollbackAsync().ConfigureAwait(false);
                blockerCompleted = true;
                var unexpectedLease = await beginTask.ConfigureAwait(false);
                await unexpectedLease.DisposeAsync().ConfigureAwait(false);
                Assert.Fail("The restricted runtime request did not wait on the controlled authority lock.");
            }

            await using var updateOwner = new NpgsqlCommand(
                "UPDATE public.managed_cases SET accountable_owner_actor_id = @owner_actor_id WHERE case_id = @case_id",
                blocker,
                blockerTransaction);
            updateOwner.Parameters.AddWithValue("owner_actor_id", fixture.NewOwnerActorId);
            updateOwner.Parameters.AddWithValue("case_id", fixture.CaseId);
            Assert.Equal(1, await updateOwner.ExecuteNonQueryAsync().ConfigureAwait(false));
            await blockerTransaction.CommitAsync().ConfigureAwait(false);
            blockerCompleted = true;
        }
        finally
        {
            if (!blockerCompleted)
            {
                await blockerTransaction.RollbackAsync().ConfigureAwait(false);
            }

            if (!beginTask.IsCompleted)
            {
                cancellation.Cancel();
            }

            try
            {
                var unexpectedLease = await beginTask.ConfigureAwait(false);
                await unexpectedLease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The expected discovery exception or cancellation has already been asserted by the test body.
            }
        }

        var exception = await Assert.ThrowsAsync<CaseLeaseDiscoveryChangedException>(async () =>
            _ = await beginTask.ConfigureAwait(false)).ConfigureAwait(false);
        Assert.Equal(fixture.CaseId, exception.CaseId);
        Assert.Equal(new[] { fixture.NewOwnerActorId, fixture.OwnerActorId }, exception.AdditionalActorHints);
        Assert.Null(unitOfWork.CurrentLease);
        Assert.Null(runtimeContext.Database.CurrentTransaction);
    }

    [CaseAuthorityPostgresFact]
    public async Task Missing_helper_and_execute_permission_fail_closed_without_owner_fallback()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        await using var profile = await CaseAuthorityRuntimeProfile.CreateAsync(fixture).ConfigureAwait(false);
        await using var runtimeContext = fixture.CreateRuntimeContext(profile);
        var unitOfWork = new PostgresCaseCommandUnitOfWork(runtimeContext);

        await using (var runtimeConnection = profile.CreateRuntimeConnection())
        {
            await runtimeConnection.OpenAsync().ConfigureAwait(false);
            await using var invalidRequest = new NpgsqlCommand(
                "SELECT swiftaid_internal.lock_case_authority(NULL::uuid, '', ARRAY['invalid'], false)", runtimeConnection);
            Assert.Equal((short)3, Assert.IsType<short>(await invalidRequest.ExecuteScalarAsync().ConfigureAwait(false)));
        }

        await using (var owner = new NpgsqlConnection(fixture.ConnectionString))
        {
            await owner.OpenAsync().ConfigureAwait(false);
            await using var revoke = new NpgsqlCommand(
                $"REVOKE EXECUTE ON FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean) FROM \"{profile.RuntimeRole}\"",
                owner);
            await revoke.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        try
        {
            await Assert.ThrowsAsync<PostgresException>(async () => await unitOfWork.BeginCaseAsync(
                    fixture.CreateActor(), fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        finally
        {
            await using var owner = new NpgsqlConnection(fixture.ConnectionString);
            await owner.OpenAsync().ConfigureAwait(false);
            await using var grant = new NpgsqlCommand(
                $"GRANT EXECUTE ON FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean) TO \"{profile.RuntimeRole}\"",
                owner);
            await grant.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        Assert.Null(unitOfWork.CurrentLease);
        Assert.Null(runtimeContext.Database.CurrentTransaction);

        await profile.DropHelperForMissingHelperScenarioAsync().ConfigureAwait(false);

        var missingHelper = await Assert.ThrowsAsync<PostgresException>(async () =>
                await unitOfWork.BeginCaseAsync(
                    fixture.CreateActor(), fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None)
                    .ConfigureAwait(false))
            .ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.UndefinedFunction, missingHelper.SqlState);
        Assert.Null(unitOfWork.CurrentLease);
        Assert.Null(runtimeContext.Database.CurrentTransaction);
    }

    [CaseAuthorityPostgresFact]
    public async Task Provisioning_failure_after_helper_commit_cleans_the_validated_partial_profile()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        string? helperRole = null;
        string? runtimeRole = null;

        var setupFailure = await Assert.ThrowsAsync<ProvisioningCheckpointFailureException>(async () =>
            await CaseAuthorityRuntimeProfile.CreateAsync(fixture, profile =>
            {
                helperRole = profile.HelperRole;
                runtimeRole = profile.RuntimeRole;
                return Task.FromException(new ProvisioningCheckpointFailureException());
            }).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.False(setupFailure.Data.Contains("RuntimeProfileCleanupFailure"));
        Assert.False(string.IsNullOrWhiteSpace(helperRole));
        Assert.False(string.IsNullOrWhiteSpace(runtimeRole));
        var roleNames = new[]
        {
            helperRole ?? throw new InvalidOperationException("The helper role was not captured."),
            runtimeRole ?? throw new InvalidOperationException("The runtime role was not captured.")
        };
        await using var owner = new NpgsqlConnection(fixture.ConnectionString);
        await owner.OpenAsync().ConfigureAwait(false);
        await using var remainingRoles = new NpgsqlCommand(
            "SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname = ANY(@roles)", owner);
        remainingRoles.Parameters.AddWithValue("roles", roleNames);
        Assert.Equal(0L, Assert.IsType<long>(await remainingRoles.ExecuteScalarAsync().ConfigureAwait(false)));
        await using var helperFunction = new NpgsqlCommand(
            "SELECT pg_catalog.to_regprocedure('swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)') IS NOT NULL",
            owner);
        Assert.False(await helperFunction.ExecuteScalarAsync().ConfigureAwait(false) is true);
    }

    [CaseAuthorityPostgresFact]
    public async Task Cleanup_preserves_an_unexpected_runtime_privilege_until_it_is_restored()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        CaseAuthorityRuntimeProfile? profile = null;
        uint runtimeRoleOid = 0;
        uint organizationOid = 0;
        var injectedGrant = false;
        Exception? primaryFailure = null;
        try
        {
            var setupFailure = await Assert.ThrowsAsync<ProvisioningCheckpointFailureException>(async () =>
                await CaseAuthorityRuntimeProfile.CreateAsync(fixture, async stagedProfile =>
                {
                    profile = stagedProfile;
                    var ownerBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false };
                    await using var ownerConnection = new NpgsqlConnection(ownerBuilder.ConnectionString);
                    await ownerConnection.OpenAsync().ConfigureAwait(false);
                    await using (var captureIdentity = new NpgsqlCommand(
                                     "SELECT (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @role), (SELECT c.oid FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relname = 'organizations')",
                                     ownerConnection))
                    {
                        captureIdentity.Parameters.AddWithValue("role", stagedProfile.RuntimeRole);
                        await using var reader = await captureIdentity.ExecuteReaderAsync().ConfigureAwait(false);
                        if (!await reader.ReadAsync().ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1))
                        {
                            throw new InvalidOperationException("Could not capture the generated role and target table identities.");
                        }

                        runtimeRoleOid = reader.GetFieldValue<uint>(0);
                        organizationOid = reader.GetFieldValue<uint>(1);
                    }

                    await using var addUnexpectedGrant = new NpgsqlCommand(
                        $"GRANT UPDATE ON TABLE public.organizations TO \"{stagedProfile.RuntimeRole}\"", ownerConnection);
                    await addUnexpectedGrant.ExecuteNonQueryAsync().ConfigureAwait(false);
                    injectedGrant = true;
                    throw new ProvisioningCheckpointFailureException();
                }).ConfigureAwait(false)).ConfigureAwait(false);

            var runtimeProfile = profile ?? throw new InvalidOperationException("The validated staging callback did not run.");
            var cleanupFailure = Assert.IsType<InvalidOperationException>(setupFailure.Data["RuntimeProfileCleanupFailure"]);
            Assert.Contains("ACL tuples differ", cleanupFailure.Message, StringComparison.Ordinal);

            var ownerBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false };
            await using var owner = new NpgsqlConnection(ownerBuilder.ConnectionString);
            await owner.OpenAsync().ConfigureAwait(false);
            await using var preservedState = new NpgsqlCommand(
                "SELECT pg_catalog.has_table_privilege(@role, 'public.organizations', 'UPDATE') AND EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @role AND oid = @role_oid)",
                owner);
            preservedState.Parameters.AddWithValue("role", runtimeProfile.RuntimeRole);
            preservedState.Parameters.AddWithValue("role_oid", NpgsqlDbType.Oid, runtimeRoleOid);
            Assert.True(await preservedState.ExecuteScalarAsync().ConfigureAwait(false) is true);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (profile is not null)
            {
                if (injectedGrant)
                {
                    try
                    {
                        await RestoreInjectedOrganizationUpdateAsync(
                            fixture, profile.RuntimeRole, runtimeRoleOid, organizationOid).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure = exception;
                    }
                }

                try
                {
                    await profile.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (cleanupFailure is null)
                    {
                        cleanupFailure = exception;
                    }
                    else
                    {
                        cleanupFailure.Data["RuntimeProfileDisposeFailure"] = exception;
                    }
                }
            }

            if (cleanupFailure is not null)
            {
                if (primaryFailure is not null)
                {
                    primaryFailure.Data["RuntimeProfileTestCleanupFailure"] = cleanupFailure;
                }
                else
                {
                    ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                }
            }
        }
    }

    private static async Task RestoreInjectedOrganizationUpdateAsync(
        CaseAuthorityPostgresFixture fixture,
        string runtimeRole,
        uint runtimeRoleOid,
        uint organizationOid)
    {
        var ownerBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false };
        await using var owner = new NpgsqlConnection(ownerBuilder.ConnectionString);
        await owner.OpenAsync().ConfigureAwait(false);
        await using (var provenance = new NpgsqlCommand(
                         "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles role JOIN pg_catalog.pg_class relation ON relation.oid = @relation_oid JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace WHERE role.rolname = @role AND role.oid = @role_oid AND role.rolcanlogin AND NOT role.rolsuper AND NOT role.rolcreatedb AND NOT role.rolcreaterole AND NOT role.rolreplication AND NOT role.rolbypassrls AND NOT role.rolinherit AND namespace.nspname = 'public' AND relation.relname = 'organizations' AND (SELECT count(*) = 1 FROM pg_catalog.aclexplode(relation.relacl) acl WHERE acl.grantee = @role_oid AND acl.grantor = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = CURRENT_USER) AND acl.privilege_type = 'UPDATE' AND NOT acl.is_grantable) AND (SELECT count(*) = 1 FROM pg_catalog.aclexplode(relation.relacl) acl WHERE acl.grantee = @role_oid AND acl.privilege_type = 'UPDATE'))",
                         owner))
        {
            provenance.Parameters.AddWithValue("role", runtimeRole);
            provenance.Parameters.AddWithValue("role_oid", NpgsqlDbType.Oid, runtimeRoleOid);
            provenance.Parameters.AddWithValue("relation_oid", NpgsqlDbType.Oid, organizationOid);
            if (await provenance.ExecuteScalarAsync().ConfigureAwait(false) is not true)
            {
                throw new InvalidOperationException("Refusing to restore the injected grant because its role, table, or ACL provenance changed.");
            }
        }

        await using var revoke = new NpgsqlCommand(
            $"REVOKE UPDATE ON TABLE public.organizations FROM \"{runtimeRole}\"", owner);
        await revoke.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    [CaseAuthorityPostgresFact]
    public async Task Begin_is_rejected_until_lease_transaction_disposal_and_tracker_cleanup_finish()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        await using var profile = await CaseAuthorityRuntimeProfile.CreateAsync(fixture).ConfigureAwait(false);
        await using var runtimeContext = fixture.CreateRuntimeContext(profile);
        using var pause = new TransactionDisposePause(runtimeContext);
        var unitOfWork = new PostgresCaseCommandUnitOfWork(runtimeContext);
        var actor = fixture.CreateActor();
        var lease = await unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Write, CancellationToken.None).ConfigureAwait(false);
        pause.SetStateObserver(() => (lease.IsActive, runtimeContext.Database.CurrentTransaction is not null));

        var commitTask = lease.CommitAsync(CancellationToken.None).AsTask();
        try
        {
            await pause.TransactionDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.True(pause.ObservedState.HasValue, "The transaction-disposed barrier did not capture lease state.");
            var (leaseIsActive, hasCurrentTransaction) = pause.ObservedState.Value;
            Assert.False(leaseIsActive, "The committed lease must be inactive before owner Release finishes.");
            Assert.False(hasCurrentTransaction, "The transaction must be committed before owner Release finishes.");
            Assert.Null(unitOfWork.CurrentLease);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await unitOfWork.BeginCaseAsync(
                    actor, fixture.SiblingCaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
            Assert.Equal("A case lease or database transaction is already active for this scoped context.", exception.Message);
        }
        finally
        {
            pause.AllowCompletion();
        }

        await commitTask.ConfigureAwait(false);
        await using var nextLease = await unitOfWork.BeginCaseAsync(
            actor, fixture.SiblingCaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(fixture.SiblingCaseId, nextLease.CaseId);
    }

    private static async Task<bool> WaitForRuntimeLockWaitAsync(
        string ownerConnectionString,
        string applicationName,
        TimeSpan timeout)
    {
        var ownerBuilder = new NpgsqlConnectionStringBuilder(ownerConnectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(ownerBuilder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name = @application_name AND wait_event_type = 'Lock' AND state = 'active')",
                connection);
            command.Parameters.AddWithValue("application_name", applicationName);
            if (await command.ExecuteScalarAsync().ConfigureAwait(false) is true)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
        }

        return false;
    }

    private sealed class TransactionDisposePause :
        IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>,
        IDisposable
    {
        private readonly TaskCompletionSource _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DbContext _context;
        private readonly IDisposable _allListenersSubscription;
        private IDisposable? _transactionSubscription;
        private int _observed;
        private Func<(bool LeaseIsActive, bool HasCurrentTransaction)>? _stateObserver;

        public TaskCompletionSource TransactionDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public (bool LeaseIsActive, bool HasCurrentTransaction)? ObservedState { get; private set; }

        public TransactionDisposePause(DbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void SetStateObserver(Func<(bool LeaseIsActive, bool HasCurrentTransaction)> stateObserver) =>
            _stateObserver = stateObserver ?? throw new ArgumentNullException(nameof(stateObserver));

        public void AllowCompletion() => _continue.TrySetResult();

        public void OnNext(DiagnosticListener listener)
        {
            if (!string.Equals(listener.Name, "Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            {
                return;
            }

            _transactionSubscription = listener.Subscribe(
                this,
                static (eventName, _, _) => eventName == RelationalEventId.TransactionDisposed.Name);
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key != RelationalEventId.TransactionDisposed.Name ||
                value.Value is not TransactionEndEventData eventData ||
                !ReferenceEquals(eventData.Context, _context) ||
                Interlocked.Exchange(ref _observed, 1) != 0)
            {
                return;
            }

            ObservedState = _stateObserver?.Invoke();
            TransactionDisposed.TrySetResult();
            _continue.Task.GetAwaiter().GetResult();
        }

        public void OnError(Exception error) => TransactionDisposed.TrySetException(error);

        public void OnCompleted() { }

        public void Dispose()
        {
            AllowCompletion();
            _transactionSubscription?.Dispose();
            _allListenersSubscription.Dispose();
        }
    }

    private sealed class ProvisioningCheckpointFailureException : Exception
    {
    }
}
