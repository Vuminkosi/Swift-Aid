using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Hosting;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task OfficerEscalationAssignmentAtomicallySchedulesApprovedFollowUpAndReplays()
    {
        // Append-only approval, assignment and designation records require a disposable database.
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
        var officers = new PostgresEligibleHealthOfficerStore(db, unit);
        var settings = new HealthOfficerDirectoryOptions { Enabled = true, EscalationApprovalsEnabled = true, EscalationAssignmentsEnabled = true };
        var approvals = new PostgresHealthOfficerEscalationApprovalService(db, unit, policy, officers, settings);
        var approved = new HealthOfficerEscalationApprovalRequest(Guid.NewGuid(), Guid.NewGuid(), fixture.CaseId, escalationId, 3,
            fixture.NewOwnerActorId, designationId, 1, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Recorded, (await approvals.ApproveAsync(fixture.CreateActor(), approved, default)).Status);
        var payloads = new PostgresProtectedCaseEventPayloadStore(db, unit, keyRing.CreateOptions(), provider);
        var persistence = new PostgresCaseCommandPersistence(db, unit, payloads, new CaseEventSerializer());
        var service = new PostgresHealthOfficerEscalationAssignmentService(db, unit, policy, persistence, officers, settings);
        var request = new HealthOfficerEscalationAssignmentRequest(fixture.CaseId, approved.ApprovalId, Guid.NewGuid());
        var expiredApproval = Guid.NewGuid();
        var expiredFollowUp = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_escalation_approvals (approval_id, command_id, case_id, escalation_id, approver_actor_id, approver_access_epoch,
                officer_actor_id, designation_id, designation_version, expected_case_version, follow_up_id, acknowledgement_due_utc,
                follow_up_due_utc, approved_at_utc, expires_at_utc)
            VALUES ({expiredApproval}, {Guid.NewGuid()}, {fixture.CaseId}, {escalationId}, {fixture.ActorId}, {fixture.AccessEpoch},
                {fixture.NewOwnerActorId}, {designationId}, 1, 3, {expiredFollowUp}, clock_timestamp() + interval '1 hour',
                clock_timestamp() + interval '1 day', clock_timestamp() - interval '2 hours', clock_timestamp() - interval '1 hour')
            """);
        Assert.Equal(HealthOfficerEscalationAssignmentStatus.Conflict, (await service.AssignAsync(fixture.CreateActor(), request with { ApprovalId = expiredApproval }, default)).Status);
        await using (var expiryDb = fixture.CreateDbContext())
        await using (var expiryTransaction = await expiryDb.Database.BeginTransactionAsync())
        {
            var deadline = await expiryDb.Database.SqlQuery<DateTimeOffset>($"SELECT acknowledgement_due_utc AS \"Value\" FROM health_officer_escalation_approvals WHERE approval_id = {expiredApproval}").SingleAsync();
            await expiryDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO health_officer_escalation_assignments (assignment_id, approval_id, execution_command_id, case_id,
                    escalation_id, follow_up_id, follow_up_event_id, officer_actor_id, assigned_by_actor_id,
                    acknowledgement_due_utc, assigned_at_utc, aggregate_version)
                VALUES ({Guid.NewGuid()}, {expiredApproval}, {Guid.NewGuid()}, {fixture.CaseId}, {escalationId}, {expiredFollowUp},
                    {Guid.NewGuid()}, {fixture.NewOwnerActorId}, {fixture.ActorId}, {deadline}, clock_timestamp(), 4)
                """);
            var expired = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => expiryDb.Database.ExecuteSqlRawAsync(
                "SET CONSTRAINTS verify_officer_assignment_at_commit IMMEDIATE"));
            Assert.Equal("P0001", expired.SqlState);
        }
        Assert.Equal(HealthOfficerEscalationAssignmentStatus.Denied, (await service.AssignAsync(fixture.CreateActor(16), request, default)).Status);
        Assert.Equal(HealthOfficerEscalationAssignmentStatus.Denied, (await service.AssignAsync(fixture.CreateActor(), request with { ApprovalId = Guid.NewGuid() }, default)).Status);
        settings.EscalationAssignmentsEnabled = false;
        Assert.Equal(HealthOfficerEscalationAssignmentStatus.Unavailable, (await service.AssignAsync(fixture.CreateActor(), request, default)).Status);
        settings.EscalationAssignmentsEnabled = true;
        await db.Actors.Where(actor => actor.ActorId == fixture.NewOwnerActorId)
            .ExecuteUpdateAsync(update => update.SetProperty(actor => actor.Status, PersistentActorStatus.Suspended));
        Assert.Equal(HealthOfficerEscalationAssignmentStatus.Denied, (await service.AssignAsync(fixture.CreateActor(), request, default)).Status);
        Assert.False(await db.ManagedCaseFollowUps.AnyAsync(item => item.FollowUpId == approved.FollowUpId));
        await db.Actors.Where(actor => actor.ActorId == fixture.NewOwnerActorId)
            .ExecuteUpdateAsync(update => update.SetProperty(actor => actor.Status, PersistentActorStatus.Active));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION swiftaid_classification.fixture_reject_officer_assignment() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'synthetic assignment persistence failure'; END $$;
            CREATE TRIGGER fixture_reject_officer_assignment BEFORE INSERT ON health_officer_escalation_assignments
                FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.fixture_reject_officer_assignment();
            """);
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => service.AssignAsync(fixture.CreateActor(), request, default));
            Assert.False(await db.ManagedCaseFollowUps.AsNoTracking().AnyAsync(item => item.FollowUpId == approved.FollowUpId));
            Assert.False(await db.CaseEvents.AsNoTracking().AnyAsync(item => item.CommandId == request.CommandId));
            Assert.Equal(3, (await db.ManagedCases.AsNoTracking().SingleAsync(item => item.CaseId == fixture.CaseId)).AggregateVersion);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER fixture_reject_officer_assignment ON health_officer_escalation_assignments;
                DROP FUNCTION swiftaid_classification.fixture_reject_officer_assignment();
                """);
        }
        await using var competingDb = fixture.CreateDbContext();
        var competingUnit = new PostgresCaseCommandUnitOfWork(competingDb);
        var competingPolicy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(competingDb, competingUnit), new PostgresCaseAccessDescriptorStore(competingDb, competingUnit));
        var competingPayloads = new PostgresProtectedCaseEventPayloadStore(competingDb, competingUnit, keyRing.CreateOptions(), provider);
        var competingPersistence = new PostgresCaseCommandPersistence(competingDb, competingUnit, competingPayloads, new CaseEventSerializer());
        var competing = new PostgresHealthOfficerEscalationAssignmentService(competingDb, competingUnit, competingPolicy, competingPersistence,
            new PostgresEligibleHealthOfficerStore(competingDb, competingUnit), settings);
        var outcomes = await Task.WhenAll(service.AssignAsync(fixture.CreateActor(), request, default), competing.AssignAsync(fixture.CreateActor(), request, default));
        var recorded = Assert.Single(outcomes, item => item.Status == HealthOfficerEscalationAssignmentStatus.Recorded);
        Assert.Single(outcomes, item => item.Status == HealthOfficerEscalationAssignmentStatus.Replayed);
        Assert.Equal(4, recorded.CaseVersion);
        Assert.NotNull(recorded.FollowUpEventId);
        var replayed = await service.AssignAsync(fixture.CreateActor(), request, default);
        Assert.Equal(HealthOfficerEscalationAssignmentStatus.Replayed, replayed.Status);
        Assert.Equal(recorded.FollowUpEventId, replayed.FollowUpEventId);
        Assert.Equal(HealthOfficerEscalationAssignmentStatus.Conflict, (await service.AssignAsync(fixture.CreateActor(), request with { ApprovalId = Guid.NewGuid() }, default)).Status);
        var followUp = await db.ManagedCaseFollowUps.AsNoTracking().SingleAsync(item => item.FollowUpId == approved.FollowUpId);
        Assert.Equal(fixture.NewOwnerActorId, followUp.AssigneeActorId);
        Assert.Equal(recorded.FollowUpEventId, followUp.CurrentScheduleEventId);
        var caseRow = await db.ManagedCases.AsNoTracking().SingleAsync(item => item.CaseId == fixture.CaseId);
        Assert.Equal(fixture.ActorId, caseRow.AccountableOwnerActorId);
        Assert.Equal(4, caseRow.AggregateVersion);
        Assert.Equal("CaseFollowUpScheduled", (await db.CaseEvents.AsNoTracking().SingleAsync(item => item.EventId == recorded.FollowUpEventId)).EventType);
        Assert.True(await db.DomainOutbox.AnyAsync(item => item.EventId == recorded.FollowUpEventId));
        Assert.False(await db.NotificationItems.AnyAsync(item => item.AcceptedEventId == recorded.FollowUpEventId));
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM health_officer_escalation_assignments WHERE approval_id = {approved.ApprovalId}").SingleAsync());
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261104160000_HealthOfficerEscalationApprovals"));
    }
}
