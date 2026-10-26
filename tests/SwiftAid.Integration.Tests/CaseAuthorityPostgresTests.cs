using System.Data.Common;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

public sealed class CaseAuthorityPostgresTests
{

    [CaseAuthorityPostgresFact]
    public async Task Missing_wrong_case_wrong_context_and_disposed_leases_fail_before_adapter_sql()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        var actor = fixture.CreateActor();
        var noLeaseCounter = new CommandCounter();
        await using (var noLeaseContext = fixture.CreateDbContext(noLeaseCounter))
        {
            var noLeaseStore = new PostgresCurrentAccessGrantStore(
                noLeaseContext,
                new FixedLeaseAccessor(null));

            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await noLeaseStore.GetCurrentGrantAsync(actor, CancellationToken.None).ConfigureAwait(false));
            Assert.Equal(0, noLeaseCounter.Count);
        }

        var primaryCounter = new CommandCounter();
        await using var primaryContext = fixture.CreateDbContext(primaryCounter);
        var unitOfWork = new PostgresCaseCommandUnitOfWork(primaryContext);
        var lease = await unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);
        var commandCountAfterBegin = primaryCounter.Count;

        var wrongActorStore = new PostgresCurrentAccessGrantStore(primaryContext, unitOfWork);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await wrongActorStore.GetCurrentGrantAsync(
                ActorContext.FromValidatedPrincipal(new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(
                    [
                        new System.Security.Claims.Claim("sub", fixture.OwnerActorId),
                        new System.Security.Claims.Claim(ActorContext.AccessEpochClaimType,
                            fixture.AccessEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    ], authenticationType: "fixture-authenticated"))),
                CancellationToken.None).ConfigureAwait(false);
        });
        Assert.Equal(commandCountAfterBegin, primaryCounter.Count);

        var wrongCaseStore = new PostgresCaseAccessDescriptorStore(primaryContext, unitOfWork);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await wrongCaseStore.GetCurrentDescriptorAsync(fixture.SiblingCaseId, CancellationToken.None).ConfigureAwait(false));
        Assert.Equal(commandCountAfterBegin, primaryCounter.Count);

        var wrongContextCounter = new CommandCounter();
        await using (var wrongContext = fixture.CreateDbContext(wrongContextCounter))
        {
            var wrongContextStore = new PostgresCaseAccessDescriptorStore(wrongContext, unitOfWork);
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await wrongContextStore.GetCurrentDescriptorAsync(fixture.CaseId, CancellationToken.None).ConfigureAwait(false));
            Assert.Equal(0, wrongContextCounter.Count);
        }

        await lease.DisposeAsync().ConfigureAwait(false);
        var commandCountAfterDispose = primaryCounter.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await wrongCaseStore.GetCurrentDescriptorAsync(fixture.CaseId, CancellationToken.None).ConfigureAwait(false));
        Assert.Equal(commandCountAfterDispose, primaryCounter.Count);
    }


    [CaseAuthorityPostgresFact]
    public async Task Grant_and_descriptor_adapters_authorize_only_the_locked_case_scope()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        var actor = fixture.CreateActor();
        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        await using var lease = await unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);

        var grantStore = new PostgresCurrentAccessGrantStore(dbContext, unitOfWork);
        var descriptorStore = new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork);
        var grant = await grantStore.GetCurrentGrantAsync(actor, CancellationToken.None).ConfigureAwait(false);
        var descriptor = await descriptorStore.GetCurrentDescriptorAsync(fixture.CaseId, CancellationToken.None).ConfigureAwait(false);

        Assert.NotNull(grant);
        Assert.Equal(fixture.AccessEpoch, grant!.AccessEpoch);
        Assert.Equal(AccessGrantStatus.Active, grant.Status);
        var group = Assert.Single(grant.Grants);
        Assert.Equal(fixture.OrganizationId, Assert.Single(group.OrganizationIds));
        Assert.Equal(fixture.WorkspaceId, Assert.Single(group.WorkspaceIds));
        Assert.Equal("sens-A", Assert.Single(group.SensitivityCodes));
        Assert.Equal(fixture.CaseId, Assert.Single(group.AssignedCaseIds));
        Assert.NotNull(descriptor);
        Assert.Equal(fixture.CaseId, descriptor.CaseId);
        Assert.Equal(new[] { fixture.CaseRegionId }, descriptor.RegionIds);

        var policy = new CaseAccessPolicy(grantStore, descriptorStore);
        var allowed = await policy.AuthorizeAsync(actor, fixture.CaseId, CaseOperation.ReadCase, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.True(allowed.IsAllowed);

        await lease.DisposeAsync().ConfigureAwait(false);
        await fixture.SetGrantWorkspaceAsync(fixture.AlternateWorkspaceId).ConfigureAwait(false);
        // A new context/lease ensures the policy reads a fresh row and fresh locked authority.
        await using var refreshedContext = fixture.CreateDbContext();
        var refreshedUnitOfWork = new PostgresCaseCommandUnitOfWork(refreshedContext);
        await using var refreshedLease = await refreshedUnitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);
        var refreshedPolicy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(refreshedContext, refreshedUnitOfWork),
            new PostgresCaseAccessDescriptorStore(refreshedContext, refreshedUnitOfWork));
        var denied = await refreshedPolicy.AuthorizeAsync(actor, fixture.CaseId, CaseOperation.ReadCase, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.False(denied.IsAllowed);
        Assert.Equal(AccessDecisionCode.WorkspaceOutsideGrant, denied.Code);
    }


    [CaseAuthorityPostgresFact]
    public async Task Active_share_bypasses_geography_only_inside_the_same_scalar_group()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(
            makeCurrentActorOwner: false,
            createActiveShare: true).ConfigureAwait(false);
        var actor = fixture.CreateActor();
        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        await using var lease = await unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);

        var grantStore = new PostgresCurrentAccessGrantStore(dbContext, unitOfWork);
        var descriptorStore = new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork);
        var grant = await grantStore.GetCurrentGrantAsync(actor, CancellationToken.None).ConfigureAwait(false);
        Assert.NotNull(grant);
        var group = Assert.Single(grant!.Grants);
        Assert.Empty(group.AssignedCaseIds);
        Assert.Contains(fixture.CaseId, group.SharedCaseIds);
        Assert.DoesNotContain(fixture.CaseRegionId, group.RegionIds);

        var policy = new CaseAccessPolicy(grantStore, descriptorStore);
        var allowed = await policy.AuthorizeAsync(actor, fixture.CaseId, CaseOperation.ReadCase, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.True(allowed.IsAllowed);
        Assert.Equal(fixture.GrantGroupId, allowed.MatchedGrantGroupId);

        await lease.DisposeAsync().ConfigureAwait(false);
        await fixture.SetGrantWorkspaceAsync(fixture.AlternateWorkspaceId).ConfigureAwait(false);
        await using var refreshedContext = fixture.CreateDbContext();
        var refreshedUnitOfWork = new PostgresCaseCommandUnitOfWork(refreshedContext);
        await using var refreshedLease = await refreshedUnitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);
        var refreshedGrant = await new PostgresCurrentAccessGrantStore(refreshedContext, refreshedUnitOfWork)
            .GetCurrentGrantAsync(actor, CancellationToken.None).ConfigureAwait(false);
        Assert.NotNull(refreshedGrant);
        Assert.Empty(Assert.Single(refreshedGrant!.Grants).SharedCaseIds);

        var refreshedPolicy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(refreshedContext, refreshedUnitOfWork),
            new PostgresCaseAccessDescriptorStore(refreshedContext, refreshedUnitOfWork));
        var denied = await refreshedPolicy.AuthorizeAsync(actor, fixture.CaseId, CaseOperation.ReadCase, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.False(denied.IsAllowed);
        Assert.Equal(AccessDecisionCode.WorkspaceOutsideGrant, denied.Code);
    }


    [CaseAuthorityPostgresFact]
    public async Task Current_epoch_actor_status_and_group_expiry_fail_closed()
    {
        await using (var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false))
        {
            var actorWithOldEpoch = fixture.CreateActor(fixture.AccessEpoch - 1);
            await using var dbContext = fixture.CreateDbContext();
            var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
            await using var lease = await unitOfWork.BeginCaseAsync(
                actorWithOldEpoch, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);
            var policy = new CaseAccessPolicy(
                new PostgresCurrentAccessGrantStore(dbContext, unitOfWork),
                new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork));
            var denied = await policy.AuthorizeAsync(actorWithOldEpoch, fixture.CaseId, CaseOperation.ReadCase, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.False(denied.IsAllowed);
            Assert.Equal(AccessDecisionCode.AccessEpochMismatch, denied.Code);
        }

        await using (var fixture = await CaseAuthorityPostgresFixture.CreateAsync(
            actorStatus: PersistentActorStatus.Suspended).ConfigureAwait(false))
        {
            var actor = fixture.CreateActor();
            await using var dbContext = fixture.CreateDbContext();
            var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
            await using var lease = await unitOfWork.BeginCaseAsync(
                actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);
            var policy = new CaseAccessPolicy(
                new PostgresCurrentAccessGrantStore(dbContext, unitOfWork),
                new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork));
            var denied = await policy.AuthorizeAsync(actor, fixture.CaseId, CaseOperation.ReadCase, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.False(denied.IsAllowed);
            Assert.Equal(AccessDecisionCode.GrantMissing, denied.Code);
        }

        await using (var fixture = await CaseAuthorityPostgresFixture.CreateAsync(
            grantExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(-1)).ConfigureAwait(false))
        {
            var actor = fixture.CreateActor();
            await using var dbContext = fixture.CreateDbContext();
            var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
            await using var lease = await unitOfWork.BeginCaseAsync(
                actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);
            var policy = new CaseAccessPolicy(
                new PostgresCurrentAccessGrantStore(dbContext, unitOfWork),
                new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork));
            var denied = await policy.AuthorizeAsync(actor, fixture.CaseId, CaseOperation.ReadCase, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.False(denied.IsAllowed);
            Assert.Equal(AccessDecisionCode.GrantExpired, denied.Code);
        }
    }

    [CaseAuthorityPostgresTheory]
    [InlineData(PersistentActorStatus.Suspended, PersistentMembershipStatus.Active, PersistentResourceStatus.Active, PersistentResourceStatus.Active)]
    [InlineData(PersistentActorStatus.Active, PersistentMembershipStatus.Suspended, PersistentResourceStatus.Active, PersistentResourceStatus.Active)]
    [InlineData(PersistentActorStatus.Active, PersistentMembershipStatus.Active, PersistentResourceStatus.Suspended, PersistentResourceStatus.Active)]
    [InlineData(PersistentActorStatus.Active, PersistentMembershipStatus.Active, PersistentResourceStatus.Active, PersistentResourceStatus.Suspended)]

    public async Task Inactive_actor_membership_organization_or_workspace_denies(
        PersistentActorStatus actorStatus,
        PersistentMembershipStatus membershipStatus,
        PersistentResourceStatus organizationStatus,
        PersistentResourceStatus workspaceStatus)
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(
            actorStatus: actorStatus,
            membershipStatus: membershipStatus,
            organizationStatus: organizationStatus,
            workspaceStatus: workspaceStatus).ConfigureAwait(false);
        var actor = fixture.CreateActor();
        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        await using var lease = await unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);
        var grant = await new PostgresCurrentAccessGrantStore(dbContext, unitOfWork)
            .GetCurrentGrantAsync(actor, CancellationToken.None).ConfigureAwait(false);

        Assert.NotNull(grant);
        Assert.Equal(AccessGrantStatus.Suspended, grant!.Status);
        Assert.Empty(grant.Grants);
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(dbContext, unitOfWork),
            new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork));
        var denied = await policy.AuthorizeAsync(actor, fixture.CaseId, CaseOperation.ReadCase, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.False(denied.IsAllowed);
        Assert.Equal(AccessDecisionCode.GrantMissing, denied.Code);
    }


    [CaseAuthorityPostgresFact]
    public async Task Group_expiry_is_capped_by_actor_membership_grant_and_active_share_expiry()
    {
        await using (var fixture = await CaseAuthorityPostgresFixture.CreateAsync(
            grantExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(10),
            actorExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(2),
            membershipExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(5)).ConfigureAwait(false))
        {
            var group = await ReadOnlyGroupAsync(fixture).ConfigureAwait(false);
            AssertExpiryWithinDatabasePrecision(fixture.ActorExpiresAtUtc!.Value, group.ExpiresAtUtc);
        }

        await using (var fixture = await CaseAuthorityPostgresFixture.CreateAsync(
            grantExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(10),
            membershipExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(2)).ConfigureAwait(false))
        {
            var group = await ReadOnlyGroupAsync(fixture).ConfigureAwait(false);
            AssertExpiryWithinDatabasePrecision(fixture.MembershipExpiresAtUtc, group.ExpiresAtUtc);
        }

        await using (var fixture = await CaseAuthorityPostgresFixture.CreateAsync(
            grantExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(2)).ConfigureAwait(false))
        {
            var group = await ReadOnlyGroupAsync(fixture).ConfigureAwait(false);
            AssertExpiryWithinDatabasePrecision(fixture.GrantExpiresAtUtc, group.ExpiresAtUtc);
        }

        await using (var fixture = await CaseAuthorityPostgresFixture.CreateAsync(
            makeCurrentActorOwner: false,
            grantExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(10),
            createActiveShare: true,
            actorExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(9),
            membershipExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(8),
            shareExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1)).ConfigureAwait(false))
        {
            var group = await ReadOnlyGroupAsync(fixture).ConfigureAwait(false);
            AssertExpiryWithinDatabasePrecision(fixture.ShareExpiresAtUtc!.Value, group.ExpiresAtUtc);
        }
    }


    [CaseAuthorityPostgresFact]
    public async Task Parent_authority_share_locks_allow_parallel_leases_on_distinct_cases()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        var actor = fixture.CreateActor();
        await using var firstContext = fixture.CreateDbContext();
        await using var secondContext = fixture.CreateDbContext();
        var firstUnitOfWork = new PostgresCaseCommandUnitOfWork(firstContext);
        var secondUnitOfWork = new PostgresCaseCommandUnitOfWork(secondContext);

        await using var firstLease = await firstUnitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Write, CancellationToken.None).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await using var secondLease = await secondUnitOfWork.BeginCaseAsync(
            actor, fixture.SiblingCaseId, [], CaseLeaseMode.Write, timeout.Token).ConfigureAwait(false);

        Assert.Equal(fixture.CaseId, firstLease.CaseId);
        Assert.Equal(fixture.SiblingCaseId, secondLease.CaseId);
        Assert.Equal(CaseLeaseMode.Write, secondLease.Mode);
    }


    [CaseAuthorityPostgresFact]
    public async Task Disposing_without_commit_releases_case_row_lock_and_commit_closes_lease()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        var actor = fixture.CreateActor();
        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var lease = await unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Write, CancellationToken.None).ConfigureAwait(false);

        await using (var contender = new NpgsqlConnection(fixture.ConnectionString))
        {
            await contender.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT case_id FROM public.managed_cases WHERE case_id = @case_id FOR UPDATE NOWAIT",
                contender);
            command.Parameters.AddWithValue("case_id", fixture.CaseId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        }

        await lease.DisposeAsync().ConfigureAwait(false);
        Assert.False(lease.IsActive);
        Assert.Null(unitOfWork.CurrentLease);

        await using (var contender = new NpgsqlConnection(fixture.ConnectionString))
        {
            await contender.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT case_id FROM public.managed_cases WHERE case_id = @case_id FOR UPDATE NOWAIT",
                contender);
            command.Parameters.AddWithValue("case_id", fixture.CaseId);
            Assert.Equal(fixture.CaseId, Assert.IsType<Guid>(await command.ExecuteScalarAsync().ConfigureAwait(false)));
        }

        var committedLease = await unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);
        await committedLease.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.False(committedLease.IsActive);
        Assert.Null(unitOfWork.CurrentLease);
    }


    [CaseAuthorityPostgresFact]
    public async Task Owner_discovery_change_rolls_back_and_returns_restart_actor_hint()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        var actor = fixture.CreateActor();
        var applicationName = $"swiftaid-sqlauth-{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = applicationName
        };
        await using var blocker = new NpgsqlConnection(fixture.ConnectionString);
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

        await using var discoveryContext = new SwiftAidDbContext(
            new DbContextOptionsBuilder<SwiftAidDbContext>().UseNpgsql(builder.ConnectionString).Options);
        var unitOfWork = new PostgresCaseCommandUnitOfWork(discoveryContext);
        using var waitCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var beginTask = unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Write, waitCancellation.Token).AsTask();

        var blockerCommitted = false;
        try
        {
            var blockedOnOrganization = await WaitForLockWaitAsync(
                fixture.ConnectionString,
                applicationName,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!blockedOnOrganization)
            {
                await blockerTransaction.RollbackAsync().ConfigureAwait(false);
                blockerCommitted = true;
                var unexpectedLease = await beginTask.ConfigureAwait(false);
                await unexpectedLease.DisposeAsync().ConfigureAwait(false);
                Assert.Fail("The case lease did not wait on the intentionally locked organization row.");
            }

            await using var updateOwner = new NpgsqlCommand(
                "UPDATE public.managed_cases SET accountable_owner_actor_id = @owner_actor_id WHERE case_id = @case_id",
                blocker,
                blockerTransaction);
            updateOwner.Parameters.AddWithValue("owner_actor_id", fixture.NewOwnerActorId);
            updateOwner.Parameters.AddWithValue("case_id", fixture.CaseId);
            Assert.Equal(1, await updateOwner.ExecuteNonQueryAsync().ConfigureAwait(false));
            await blockerTransaction.CommitAsync().ConfigureAwait(false);
            blockerCommitted = true;
        }
        finally
        {
            if (!blockerCommitted)
            {
                await blockerTransaction.RollbackAsync().ConfigureAwait(false);
            }
        }

        var exception = await Assert.ThrowsAsync<CaseLeaseDiscoveryChangedException>(async () =>
        {
            _ = await beginTask.ConfigureAwait(false);
        });
        Assert.Equal(fixture.CaseId, exception.CaseId);
        Assert.Contains(fixture.NewOwnerActorId, exception.AdditionalActorHints);
        Assert.Null(unitOfWork.CurrentLease);
    }


    [CaseAuthorityPostgresFact]
    public async Task Pre_cancelled_lease_request_performs_no_database_command()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        var commandCounter = new CommandCounter();
        await using var dbContext = fixture.CreateDbContext(commandCounter);
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await unitOfWork.BeginCaseAsync(
                fixture.CreateActor(), fixture.CaseId, [], CaseLeaseMode.Read, cancellation.Token).AsTask().ConfigureAwait(false));

        Assert.Equal(0, commandCounter.Count);
        Assert.Null(dbContext.Database.CurrentTransaction);
    }

    private static async Task<bool> WaitForLockWaitAsync(
        string connectionString,
        string applicationName,
        TimeSpan timeout)
    {
        await using var connection = new NpgsqlConnection(connectionString);
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

    private static async Task<ScopedAccessGrant> ReadOnlyGroupAsync(CaseAuthorityPostgresFixture fixture)
    {
        var actor = fixture.CreateActor();
        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        await using var lease = await unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Read, CancellationToken.None).ConfigureAwait(false);
        var grant = await new PostgresCurrentAccessGrantStore(dbContext, unitOfWork)
            .GetCurrentGrantAsync(actor, CancellationToken.None).ConfigureAwait(false);
        Assert.NotNull(grant);
        return Assert.Single(grant!.Grants);
    }

    private static void AssertExpiryWithinDatabasePrecision(DateTimeOffset expected, DateTimeOffset actual)
    {
        Assert.InRange((expected - actual).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
    }

    private sealed class FixedLeaseAccessor(ICaseCommandLease? lease) : ICaseCommandLeaseAccessor
    {
        public ICaseCommandLease? CurrentLease { get; } = lease;
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
