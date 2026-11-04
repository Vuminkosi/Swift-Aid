using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Authorization;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task DesignationAdministrationRequiresScopedAuthorityAndRecordsIdempotentChanges()
    {
        // Append-only records remain in this disposable database until the container is removed.
        var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        var targetGrant = await AddEligibleTargetGrantAsync(fixture);
        await AddOperationAsync(fixture, CaseOperation.ManageEscalation);
        var options = new HealthOfficerDirectoryOptions { Enabled = true, AdministrationEnabled = true };
        await using var db = fixture.CreateDbContext();
        await db.AccessGrantGroups.Where(group => group.GrantGroupId == targetGrant)
            .ExecuteUpdateAsync(update => update.SetProperty(group => group.CanAccessJurisdictionalCases, true));
        db.AccessGrantRegions.Add(new AccessGrantRegionEntity { GrantGroupId = targetGrant, RegionId = fixture.CaseRegionId });
        db.AccessGrantOperations.AddRange(
            new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageEscalation },
            new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageFollowUp });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new PostgresHealthOfficerDesignationAdministration(db, options);
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
        var lookup = new EligibleHealthOfficerQuery(new PostgresEligibleHealthOfficerStore(db, unit), policy, unit, options);
        var now = DateTimeOffset.UtcNow;
        var request = new HealthOfficerDesignationChange(Guid.NewGuid(), fixture.NewOwnerActorId, fixture.OrganizationId,
            "SZ", "HumanHealth", "Active", now.AddMinutes(-1), now.AddDays(1), 0);
        Assert.Equal(HealthOfficerDesignationChangeStatus.Denied, (await service.ChangeAsync(fixture.CreateActor(), request, default)).Status);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designation_administrators (administrator_event_id, actor_id, organization_id, country_code,
                sector, status, valid_from_utc, expires_at_utc, granted_by_actor_id, version, recorded_at_utc)
            VALUES ({Guid.NewGuid()}, {fixture.ActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Active',
                clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.OwnerActorId}, 1, clock_timestamp())
            """);
        Assert.Equal(HealthOfficerDesignationChangeStatus.Denied, (await service.ChangeAsync(fixture.CreateActor(16), request, default)).Status);
        Assert.Equal(HealthOfficerDesignationChangeStatus.Invalid, (await service.ChangeAsync(fixture.CreateActor(), request with { TargetActorId = fixture.ActorId }, default)).Status);
        Assert.Equal(HealthOfficerDesignationChangeStatus.Denied, (await service.ChangeAsync(fixture.CreateActor(), request with { Sector = "AnimalHealth" }, default)).Status);
        options.AdministrationEnabled = false;
        Assert.Equal(HealthOfficerDesignationChangeStatus.Unavailable, (await service.ChangeAsync(fixture.CreateActor(), request, default)).Status);
        options.AdministrationEnabled = true;

        await using var otherDb = fixture.CreateDbContext();
        var otherService = new PostgresHealthOfficerDesignationAdministration(otherDb, options);
        var concurrent = await Task.WhenAll(service.ChangeAsync(fixture.CreateActor(), request, default),
            otherService.ChangeAsync(fixture.CreateActor(), request, default));
        Assert.Equal(1, concurrent.Count(result => result.Status == HealthOfficerDesignationChangeStatus.Recorded));
        Assert.Equal(1, concurrent.Count(result => result.Status == HealthOfficerDesignationChangeStatus.Replayed));
        Assert.Equal(concurrent[0].DesignationId, concurrent[1].DesignationId);
        Assert.Equal(fixture.NewOwnerActorId, Assert.Single((await lookup.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items).ActorId);
        Assert.Equal(HealthOfficerDesignationChangeStatus.Conflict, (await service.ChangeAsync(fixture.CreateActor(), request with { Status = "Suspended" }, default)).Status);
        Assert.Equal(HealthOfficerDesignationChangeStatus.Conflict, (await service.ChangeAsync(fixture.CreateActor(), request with { CommandId = Guid.NewGuid() }, default)).Status);
        var revoke = request with { CommandId = Guid.NewGuid(), Status = "Revoked", ExpectedVersion = 1 };
        var revoked = await service.ChangeAsync(fixture.CreateActor(), revoke, default);
        Assert.Equal(HealthOfficerDesignationChangeStatus.Recorded, revoked.Status);
        Assert.Equal(2, revoked.Version);
        Assert.Empty((await lookup.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default))!.Items);
        Assert.Equal(HealthOfficerDesignationChangeStatus.Replayed, (await service.ChangeAsync(fixture.CreateActor(), revoke, default)).Status);
        var reactivate = request with { CommandId = Guid.NewGuid(), ExpectedVersion = 2 };
        var competing = await Task.WhenAll(service.ChangeAsync(fixture.CreateActor(), reactivate, default),
            otherService.ChangeAsync(fixture.CreateActor(), reactivate with { CommandId = Guid.NewGuid() }, default));
        Assert.Equal(1, competing.Count(result => result.Status == HealthOfficerDesignationChangeStatus.Recorded));
        Assert.Equal(1, competing.Count(result => result.Status == HealthOfficerDesignationChangeStatus.Conflict));
        await db.Actors.Where(actor => actor.ActorId == fixture.ActorId)
            .ExecuteUpdateAsync(update => update.SetProperty(actor => actor.Status, PersistentActorStatus.Suspended));
        Assert.Equal(HealthOfficerDesignationChangeStatus.Denied, (await service.ChangeAsync(fixture.CreateActor(), revoke, default)).Status);
        await db.Actors.Where(actor => actor.ActorId == fixture.ActorId)
            .ExecuteUpdateAsync(update => update.SetProperty(actor => actor.Status, PersistentActorStatus.Active));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE health_officer_designation_commands SET status = 'Active' WHERE command_id = {revoke.CommandId}"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261104140000_HealthOfficerDesignations"));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designation_administrators (administrator_event_id, actor_id, organization_id, country_code,
                sector, status, valid_from_utc, expires_at_utc, granted_by_actor_id, version, recorded_at_utc)
            VALUES ({Guid.NewGuid()}, {fixture.ActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Revoked',
                clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.OwnerActorId}, 2, clock_timestamp())
            """);
        Assert.Equal(HealthOfficerDesignationChangeStatus.Denied, (await service.ChangeAsync(fixture.CreateActor(), revoke, default)).Status);
    }
}
