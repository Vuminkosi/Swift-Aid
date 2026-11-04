using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Hosting;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task OfficerEscalationApprovalBindsCurrentCaseOfficerAndDeadlinesWithoutAssignment()
    {
        // The immutable approval and designation are retained only in a disposable fixture database.
        var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        await AddOperationsAsync(fixture, [CaseOperation.ManageEscalation, CaseOperation.ManageFollowUp]);
        var targetGrant = await AddEligibleTargetGrantAsync(fixture);
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider);
        var escalationId = Guid.NewGuid();
        var raised = await ProcessCommandAsync(fixture, fixture.CreateActor(),
            new RaiseEscalationCommand(Guid.NewGuid(), 2, escalationId, "synthetic review", [], "synthetic-route", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, raised.Status);
        var designationId = Guid.NewGuid();
        await using var db = fixture.CreateDbContext();
        await db.AccessGrantGroups.Where(group => group.GrantGroupId == targetGrant)
            .ExecuteUpdateAsync(update => update.SetProperty(group => group.CanAccessJurisdictionalCases, true));
        db.AccessGrantRegions.Add(new AccessGrantRegionEntity { GrantGroupId = targetGrant, RegionId = fixture.CaseRegionId });
        db.AccessGrantOperations.AddRange(
            new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageEscalation },
            new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageFollowUp });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
            VALUES ({designationId}, {fixture.NewOwnerActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Active',
                clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.ActorId}, 1, clock_timestamp(), clock_timestamp())
            """);
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
        var settings = new HealthOfficerDirectoryOptions { Enabled = true, EscalationApprovalsEnabled = true };
        var service = new PostgresHealthOfficerEscalationApprovalService(db, unit, policy, new PostgresEligibleHealthOfficerStore(db, unit), settings);
        var request = new HealthOfficerEscalationApprovalRequest(Guid.NewGuid(), Guid.NewGuid(), fixture.CaseId, escalationId, 3,
            fixture.NewOwnerActorId, designationId, 1, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddDays(1));
        settings.EscalationApprovalsEnabled = false;
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Unavailable, (await service.ApproveAsync(fixture.CreateActor(), request, default)).Status);
        settings.EscalationApprovalsEnabled = true;
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Denied, (await service.ApproveAsync(fixture.CreateActor(16), request, default)).Status);
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Conflict, (await service.ApproveAsync(fixture.CreateActor(), request with { ExpectedCaseVersion = 2 }, default)).Status);
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Denied, (await service.ApproveAsync(fixture.CreateActor(), request with { DesignationVersion = 2 }, default)).Status);
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Invalid, (await service.ApproveAsync(fixture.CreateActor(), request with { FollowUpDueUtc = request.AcknowledgementDueUtc.AddMinutes(-1) }, default)).Status);
        await using var competingDb = fixture.CreateDbContext();
        var competingUnit = new PostgresCaseCommandUnitOfWork(competingDb);
        var competingPolicy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(competingDb, competingUnit), new PostgresCaseAccessDescriptorStore(competingDb, competingUnit));
        var competingService = new PostgresHealthOfficerEscalationApprovalService(competingDb, competingUnit, competingPolicy,
            new PostgresEligibleHealthOfficerStore(competingDb, competingUnit), settings);
        var outcomes = await Task.WhenAll(service.ApproveAsync(fixture.CreateActor(), request, default),
            competingService.ApproveAsync(fixture.CreateActor(), request, default));
        Assert.Equal(1, outcomes.Count(outcome => outcome.Status == HealthOfficerEscalationApprovalStatus.Recorded));
        Assert.Equal(1, outcomes.Count(outcome => outcome.Status == HealthOfficerEscalationApprovalStatus.Replayed));
        Assert.All(outcomes, outcome => Assert.Equal(request.ApprovalId, outcome.ApprovalId));
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Replayed, (await service.ApproveAsync(fixture.CreateActor(), request, default)).Status);
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Conflict, (await service.ApproveAsync(fixture.CreateActor(), request with { FollowUpId = Guid.NewGuid() }, default)).Status);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
            VALUES ({Guid.NewGuid()}, {fixture.NewOwnerActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Revoked',
                clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.ActorId}, 2, clock_timestamp(), clock_timestamp())
            """);
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Denied, (await service.ApproveAsync(fixture.CreateActor(),
            request with { ApprovalId = Guid.NewGuid(), CommandId = Guid.NewGuid() }, default)).Status);
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Replayed, (await service.ApproveAsync(fixture.CreateActor(), request, default)).Status);
        Assert.False(await db.ManagedCaseFollowUps.AnyAsync(item => item.FollowUpId == request.FollowUpId));
        Assert.Equal(3, (await db.ManagedCases.SingleAsync(item => item.CaseId == fixture.CaseId)).AggregateVersion);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE health_officer_escalation_approvals SET officer_actor_id = {fixture.ActorId} WHERE approval_id = {request.ApprovalId}"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261104150000_HealthOfficerDesignationAdministration"));
    }
}
