using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Hosting;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task OfficerLookupRequiresDesignationSingleGrantAndEveryCaseRegion()
    {
        // Append-only designation rows are retained; run only in a disposable database.
        var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        var targetGrant = await AddEligibleTargetGrantAsync(fixture);
        await AddOperationAsync(fixture, CaseOperation.ManageEscalation);
        await using var db = fixture.CreateDbContext();
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
        var store = new PostgresEligibleHealthOfficerStore(db, unit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(fixture.CaseId, 20, default));
        await using (var lease = await unit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId, [], CaseLeaseMode.Read, default))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(Guid.NewGuid(), 20, default));
            await using var otherDb = fixture.CreateDbContext();
            var wrongContextStore = new PostgresEligibleHealthOfficerStore(otherDb, unit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrongContextStore.GetAsync(fixture.CaseId, 20, default));
        }
        await using (var lease = await unit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId, [], CaseLeaseMode.Write, default))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(fixture.CaseId, 20, default));
        }
        var query = new EligibleHealthOfficerQuery(store, policy, unit, new HealthOfficerDirectoryOptions { Enabled = true });
        var disabled = new EligibleHealthOfficerQuery(store, policy, unit, new HealthOfficerDirectoryOptions());
        Assert.Null(await disabled.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default));
        Assert.Empty((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items);
        var designationId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
            VALUES ({designationId}, {fixture.NewOwnerActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Active',
                clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.ActorId}, 1, clock_timestamp(), clock_timestamp())
            """);
        Assert.Empty((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items);
        await db.AccessGrantGroups.Where(group => group.GrantGroupId == targetGrant)
            .ExecuteUpdateAsync(update => update.SetProperty(group => group.CanAccessJurisdictionalCases, true));
        var original = await db.AccessGrantGroups.SingleAsync(group => group.GrantGroupId == targetGrant);
        var split = (AccessGrantGroupEntity)db.Entry(original).CurrentValues.ToObject();
        split.GrantGroupId = targetGrant + "-split";
        db.AccessGrantGroups.Add(split);
        db.AccessGrantOperations.AddRange(
            new AccessGrantOperationEntity { GrantGroupId = split.GrantGroupId, Operation = CaseOperation.ManageEscalation },
            new AccessGrantOperationEntity { GrantGroupId = split.GrantGroupId, Operation = CaseOperation.ManageFollowUp });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Empty((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items);
        db.AccessGrantRegions.AddRange(
            new AccessGrantRegionEntity { GrantGroupId = targetGrant, RegionId = fixture.CaseRegionId },
            new AccessGrantRegionEntity { GrantGroupId = split.GrantGroupId, RegionId = fixture.CaseRegionId });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Empty((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items);
        db.AccessGrantOperations.AddRange(
            new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageEscalation },
            new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageFollowUp });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var officer = Assert.Single((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items);
        Assert.Equal(fixture.NewOwnerActorId, officer.ActorId);
        Assert.Equal(designationId, officer.DesignationId);
        Assert.Equal(1, officer.DesignationVersion);
        Assert.Null(officer.DisplayLabel);
        await using (var readLease = await unit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId, [fixture.NewOwnerActorId], CaseLeaseMode.Read, default))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.IsEligibleForAssignmentAsync(readLease, fixture.NewOwnerActorId, designationId, 1, default));
        }
        await using (var missingTarget = await unit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId, [], CaseLeaseMode.Write, default))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.IsEligibleForAssignmentAsync(missingTarget, fixture.NewOwnerActorId, designationId, 1, default));
        }
        await using (var assignmentLease = await unit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId, [fixture.NewOwnerActorId], CaseLeaseMode.Write, default))
        {
            Assert.True(await store.IsEligibleForAssignmentAsync(assignmentLease, fixture.NewOwnerActorId, designationId, 1, default));
            Assert.False(await store.IsEligibleForAssignmentAsync(assignmentLease, fixture.NewOwnerActorId, designationId, 2, default));
            Assert.False(await store.IsEligibleForAssignmentAsync(assignmentLease, fixture.NewOwnerActorId, Guid.NewGuid(), 1, default));
            await using var competing = fixture.CreateDbContext();
            await using var competingTransaction = await competing.Database.BeginTransactionAsync();
            var acquired = await competing.Database.SqlQuery<bool>($"""
                SELECT pg_try_advisory_xact_lock(hashtextextended('officer-designation:' ||
                    jsonb_build_array({fixture.NewOwnerActorId}, c.organization_id, c.country_code, c.sector)::text, 0)) AS "Value"
                FROM managed_cases c WHERE c.case_id = {fixture.CaseId}
                """).SingleAsync();
            Assert.False(acquired);
            await competing.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '200ms'");
            var blockedChange = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => competing.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                    valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
                VALUES ({Guid.NewGuid()}, {fixture.NewOwnerActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Revoked',
                    clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.ActorId}, 2, clock_timestamp(), clock_timestamp())
                """));
            Assert.Equal("55P03", blockedChange.SqlState);
        }
        await db.Actors.Where(actor => actor.ActorId == fixture.NewOwnerActorId)
            .ExecuteUpdateAsync(update => update.SetProperty(actor => actor.Status, PersistentActorStatus.Suspended));
        Assert.Empty((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items);
        await db.Actors.Where(actor => actor.ActorId == fixture.NewOwnerActorId)
            .ExecuteUpdateAsync(update => update.SetProperty(actor => actor.Status, PersistentActorStatus.Active));
        db.ManagedCaseRegions.Add(new ManagedCaseRegionEntity { CaseId = fixture.CaseId, RegionId = "extra-region" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Empty((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items);
        db.AccessGrantRegions.Add(new AccessGrantRegionEntity { GrantGroupId = targetGrant, RegionId = "extra-region" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Single((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
            VALUES ({Guid.NewGuid()}, {fixture.NewOwnerActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Active',
                clock_timestamp() - interval '2 days', clock_timestamp() - interval '1 day', {fixture.ActorId}, 2, clock_timestamp(), clock_timestamp())
            """);
        Assert.Empty((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
            VALUES ({Guid.NewGuid()}, {fixture.NewOwnerActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Active',
                clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.ActorId}, 3, clock_timestamp(), clock_timestamp())
            """);
        Assert.Equal(3, Assert.Single((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items).DesignationVersion);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
            VALUES ({Guid.NewGuid()}, {fixture.NewOwnerActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Revoked',
                clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.ActorId}, 4, clock_timestamp(), clock_timestamp())
            """);
        Assert.Empty((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items);
        await using (var assignmentLease = await unit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId, [fixture.NewOwnerActorId], CaseLeaseMode.Write, default))
            Assert.False(await store.IsEligibleForAssignmentAsync(assignmentLease, fixture.NewOwnerActorId, designationId, 1, default));
        Assert.Null(await query.GetAsync(CaseRegistrationBackendTests.CreateOfficer("unknown-" + Guid.NewGuid()), fixture.CaseId, 20, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(fixture.CaseId, 20, default));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE health_officer_designations SET status = 'Revoked' WHERE designation_id = {designationId}"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261104130000_EllaiFollowUpReviews"));
    }

    [CaseAuthorityPostgresFact]
    public async Task Follow_up_schedule_denies_recipient_with_read_only_grant()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        cleanup.TrackTargetGrant(await AddEligibleTargetGrantAsync(fixture));
        await AddOperationAsync(fixture, CaseOperation.ManageFollowUp);
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider);
        var command = new ScheduleFollowUpCommand(Guid.NewGuid(), 2, Guid.NewGuid(),
            "Synthetic follow-up", fixture.NewOwnerActorId, DateTimeOffset.UtcNow.AddDays(1),
            "Synthetic reason", null, DateTimeOffset.UtcNow);

        var result = await ProcessCommandAsync(fixture, fixture.CreateActor(), command, keyRing.CreateOptions(), provider);

        Assert.Equal(CaseCommandProcessingStatus.Denied, result.Status);
        await using var verification = fixture.CreateDbContext();
        Assert.False(await verification.CaseEvents.AnyAsync(item => item.CaseId == fixture.CaseId && item.CommandId == command.CommandId));
        Assert.False(await verification.ManagedCaseFollowUps.AnyAsync(item => item.FollowUpId == command.FollowUpId));
        Assert.False(await verification.CommandReceipts.AnyAsync(item => item.CommandId == command.CommandId));
    }

    [CaseAuthorityPostgresFact]
    public async Task Collaborator_candidates_require_management_and_keep_permission_groups_separate_across_pages()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        await using var cleanup = new CaseCommandProcessingCleanup(fixture);
        var targetKey = await AddEligibleTargetGrantAsync(fixture);
        cleanup.TrackTargetGrant(targetKey);

        async Task<bool> QueryIsAllowed()
        {
            await using var db = fixture.CreateDbContext();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
            var query = new CaseCollaboratorCandidatesQuery(new PostgresCaseCollaboratorCandidatesStore(db), policy, unit);
            var result = await query.GetAsync(fixture.CreateActor(), fixture.CaseId, null, null, 25, CancellationToken.None);
            if (result is not null) Assert.Equal(fixture.NewOwnerActorId, Assert.Single(result.Items).ActorId);
            return result is not null;
        }

        Assert.False(await QueryIsAllowed());
        await AddOperationAsync(fixture, CaseOperation.ManageCollaborators);
        Assert.True(await QueryIsAllowed());
        await AddOperationAsync(fixture, CaseOperation.ManageFollowUp);
        await using (var followUpDb = fixture.CreateDbContext())
        {
            var followUpUnit = new PostgresCaseCommandUnitOfWork(followUpDb);
            var followUpPolicy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(followUpDb, followUpUnit), new PostgresCaseAccessDescriptorStore(followUpDb, followUpUnit));
            var followUpQuery = new CaseCollaboratorCandidatesQuery(new PostgresCaseCollaboratorCandidatesStore(followUpDb), followUpPolicy, followUpUnit);
            var followUpPage = await followUpQuery.GetAsync(fixture.CreateActor(), fixture.CaseId, null, null, 25, CancellationToken.None, followUpAssignees: true);
            Assert.NotNull(followUpPage);
            Assert.Empty(followUpPage!.Items); // The recipient has ReadCase only until the eligible grant below is added.
        }
        await using var context = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(context);
        await using var lease = await unitOfWork.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId,
            [fixture.NewOwnerActorId], CaseLeaseMode.Write, CancellationToken.None);
        var source = await context.AccessGrantGroups.SingleAsync(item => item.GrantGroupId == targetKey);
        var second = (AccessGrantGroupEntity)context.Entry(source).CurrentValues.ToObject();
        second.GrantGroupId = targetKey + "-second";
        context.AccessGrantGroups.Add(second);
        context.AccessGrantOperations.AddRange(
            new AccessGrantOperationEntity { GrantGroupId = second.GrantGroupId, Operation = CaseOperation.ReadCase },
            new AccessGrantOperationEntity { GrantGroupId = second.GrantGroupId, Operation = CaseOperation.ManageFollowUp },
            new AccessGrantOperationEntity { GrantGroupId = second.GrantGroupId, Operation = CaseOperation.ManageCollaborators });
        await context.SaveChangesAsync();
        var store = new PostgresCaseCollaboratorCandidatesStore(context);
        var firstPage = await store.GetAsync(fixture.CaseId, "", null, 1, CancellationToken.None);
        var first = Assert.Single(firstPage.Items);
        Assert.Equal(targetKey, first.Key);
        Assert.Equal(["ReadCase"], first.Operations.ToArray());
        Assert.Null(first.SafeDisplayLabel);
        Assert.Equal(first.Key, firstPage.NextCursor);
        var secondPage = await store.GetAsync(fixture.CaseId, "", firstPage.NextCursor, 1, CancellationToken.None);
        var next = Assert.Single(secondPage.Items);
        Assert.Equal(second.GrantGroupId, next.Key);
        Assert.Equal(["ReadCase", "ManageFollowUp"], next.Operations.ToArray());
        var followUpCandidates = await store.GetAsync(fixture.CaseId, "", null, 25, CancellationToken.None, followUpAssignees: true);
        var followUpCandidate = Assert.Single(followUpCandidates.Items);
        Assert.Equal(second.GrantGroupId, followUpCandidate.Key);
        Assert.Equal(["ReadCase", "ManageFollowUp"], followUpCandidate.Operations.ToArray());
        Assert.Null(secondPage.NextCursor);
        Assert.Empty((await store.GetAsync(fixture.CaseId, "absent-name", null, 25, CancellationToken.None)).Items);

        source.CountryCode = "ZA";
        second.SensitivityCode = "different-scope";
        await context.SaveChangesAsync();
        Assert.Empty((await store.GetAsync(fixture.CaseId, "", null, 25, CancellationToken.None)).Items);
        source.CountryCode = "SZ";
        second.SensitivityCode = "sens-A";
        source.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        second.Status = PersistentGrantStatus.Revoked;
        await context.SaveChangesAsync();
        Assert.Empty((await store.GetAsync(fixture.CaseId, "", null, 25, CancellationToken.None)).Items);
        source.ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1);
        await context.SaveChangesAsync();
        Assert.Single((await store.GetAsync(fixture.CaseId, "", null, 25, CancellationToken.None)).Items);
        second.Status = PersistentGrantStatus.Active;
        second.ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1);
        await context.SaveChangesAsync();
        context.CaseShares.Add(new CaseShareEntity
        {
            CaseId = fixture.CaseId, ActorId = fixture.NewOwnerActorId, GrantedByActorId = fixture.ActorId,
            Version = 1, IsActive = true, GrantedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();
        Assert.Empty((await store.GetAsync(fixture.CaseId, "", null, 25, CancellationToken.None)).Items);
        Assert.Single((await store.GetAsync(fixture.CaseId, "", null, 25, CancellationToken.None, followUpAssignees: true)).Items);
        // All altered/extra grants and the share are rolled back before fixture cleanup.
    }
}
