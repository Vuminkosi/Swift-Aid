using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
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
using SwiftAid.Gateway.Cases;
using SwiftAid.Contracts.Cases;

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
        var reviewStore = new PostgresHealthOfficerEscalationReviewStore(db, unit);
        await using (var lease = await unit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId, [], CaseLeaseMode.Write, default))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reviewStore.GetAsync(lease, fixture.CaseId, default));
        await using (var lease = await unit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId, [], CaseLeaseMode.Read, default))
            await Assert.ThrowsAsync<InvalidOperationException>(() => reviewStore.GetAsync(lease, Guid.NewGuid(), default));
        var review = new HealthOfficerEscalationReviewQuery(unit, policy, reviewStore, officers, settings);
        Assert.Null(await review.GetAsync(fixture.CreateActor(16), fixture.CaseId, default));
        var reviewContext = await review.GetAsync(fixture.CreateActor(), fixture.CaseId, default);
        Assert.Equal(3, reviewContext!.CaseVersion);
        Assert.Equal(escalationId, Assert.Single(reviewContext.Escalations).EscalationId);
        Assert.Equal(designationId, Assert.Single(reviewContext.Officers).DesignationId);
        await db.AccessGrantOperations.Where(item => item.GrantGroupId == fixture.GrantGroupId && item.Operation == CaseOperation.ManageFollowUp)
            .ExecuteDeleteAsync();
        Assert.Null(await review.GetAsync(fixture.CreateActor(), fixture.CaseId, default));
        await AddOperationAsync(fixture, CaseOperation.ManageFollowUp);
        settings.EscalationAssignmentsEnabled = false;
        Assert.Null(await review.GetAsync(fixture.CreateActor(), fixture.CaseId, default));
        settings.EscalationAssignmentsEnabled = true;
        var approvals = new PostgresHealthOfficerEscalationApprovalService(db, unit, policy, officers, settings);
        var approved = new HealthOfficerEscalationApprovalRequest(Guid.NewGuid(), Guid.NewGuid(), fixture.CaseId, escalationId, 3,
            fixture.NewOwnerActorId, designationId, 1, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddDays(1));
        await using var gatewayServices = new ServiceCollection()
            .AddScoped(_ => fixture.CreateDbContext())
            .AddScoped<IHealthOfficerEscalationReviewQuery>(services =>
            {
                var scopedDb = services.GetRequiredService<SwiftAidDbContext>();
                var scopedUnit = new PostgresCaseCommandUnitOfWork(scopedDb);
                var scopedPolicy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(scopedDb, scopedUnit), new PostgresCaseAccessDescriptorStore(scopedDb, scopedUnit));
                return new HealthOfficerEscalationReviewQuery(scopedUnit, scopedPolicy, new PostgresHealthOfficerEscalationReviewStore(scopedDb, scopedUnit),
                    new PostgresEligibleHealthOfficerStore(scopedDb, scopedUnit), settings);
            })
            .AddScoped<IHealthOfficerEscalationApprovalService>(services =>
            {
                var scopedDb = services.GetRequiredService<SwiftAidDbContext>();
                var scopedUnit = new PostgresCaseCommandUnitOfWork(scopedDb);
                var scopedPolicy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(scopedDb, scopedUnit), new PostgresCaseAccessDescriptorStore(scopedDb, scopedUnit));
                return new PostgresHealthOfficerEscalationApprovalService(scopedDb, scopedUnit, scopedPolicy, new PostgresEligibleHealthOfficerStore(scopedDb, scopedUnit), settings);
            })
            .AddScoped<IHealthOfficerEscalationAssignmentService>(services =>
            {
                var scopedDb = services.GetRequiredService<SwiftAidDbContext>();
                var scopedUnit = new PostgresCaseCommandUnitOfWork(scopedDb);
                var scopedPolicy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(scopedDb, scopedUnit), new PostgresCaseAccessDescriptorStore(scopedDb, scopedUnit));
                var scopedPayload = new PostgresProtectedCaseEventPayloadStore(scopedDb, scopedUnit, keyRing.CreateOptions(), provider);
                return new PostgresHealthOfficerEscalationAssignmentService(scopedDb, scopedUnit, scopedPolicy,
                    new PostgresCaseCommandPersistence(scopedDb, scopedUnit, scopedPayload, new CaseEventSerializer()),
                    new PostgresEligibleHealthOfficerStore(scopedDb, scopedUnit), settings);
            }).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var gateway = new OfficerEscalationReviewGatewayClient(new FixtureOfficerAuth(Principal(fixture.ActorId, fixture.AccessEpoch)), gatewayServices);
        var gatewayContext = await gateway.GetAsync(fixture.CaseId);
        Assert.Equal(escalationId, Assert.Single(gatewayContext!.Escalations).EscalationId);
        var assignmentCommand = Guid.NewGuid();
        var reviewed = new OfficerEscalationReviewedSelection(fixture.CaseId, approved.ExpectedCaseVersion, escalationId,
            approved.OfficerActorId, designationId, 1, approved.FollowUpId, approved.AcknowledgementDueUtc, approved.FollowUpDueUtc,
            approved.ApprovalId, approved.CommandId, assignmentCommand);
        Assert.Equal(OfficerEscalationActionStatus.Recorded, (await gateway.ApproveAsync(reviewed)).Status);
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Replayed, (await approvals.ApproveAsync(fixture.CreateActor(), approved, default)).Status);
        var payloads = new PostgresProtectedCaseEventPayloadStore(db, unit, keyRing.CreateOptions(), provider);
        var persistence = new PostgresCaseCommandPersistence(db, unit, payloads, new CaseEventSerializer());
        var service = new PostgresHealthOfficerEscalationAssignmentService(db, unit, policy, persistence, officers, settings);
        var request = new HealthOfficerEscalationAssignmentRequest(fixture.CaseId, approved.ApprovalId, assignmentCommand);
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
        var directTask = service.AssignAsync(fixture.CreateActor(), request, default);
        var webTask = gateway.AssignAsync(reviewed);
        await Task.WhenAll(directTask, webTask);
        var direct = await directTask;
        var web = await webTask;
        Assert.True(direct.Status == HealthOfficerEscalationAssignmentStatus.Recorded && web.Status == OfficerEscalationActionStatus.Replayed ||
            direct.Status == HealthOfficerEscalationAssignmentStatus.Replayed && web.Status == OfficerEscalationActionStatus.Recorded);
        var recorded = direct.Status == HealthOfficerEscalationAssignmentStatus.Recorded ? direct :
            new HealthOfficerEscalationAssignmentResult(HealthOfficerEscalationAssignmentStatus.Recorded, web.EventId, web.CaseVersion);
        Assert.Equal(4, recorded.CaseVersion);
        Assert.NotNull(recorded.FollowUpEventId);
        var replayed = await service.AssignAsync(fixture.CreateActor(), request, default);
        Assert.Equal(HealthOfficerEscalationAssignmentStatus.Replayed, replayed.Status);
        Assert.Equal(recorded.FollowUpEventId, replayed.FollowUpEventId);
        var gatewayReplay = await gateway.AssignAsync(reviewed);
        Assert.Equal(OfficerEscalationActionStatus.Replayed, gatewayReplay.Status);
        Assert.Equal(recorded.FollowUpEventId, gatewayReplay.EventId);
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
        var afterAssignment = await review.GetAsync(fixture.CreateActor(), fixture.CaseId, default);
        Assert.Equal(4, afterAssignment!.CaseVersion);
        Assert.Empty(afterAssignment.Escalations);
        var timelineStore = new PostgresCaseTimelineStore(db);
        await using (var readLease = await unit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId, [], CaseLeaseMode.Read, default))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => timelineStore.GetOfficerEscalationsAsync(readLease, Guid.NewGuid(), [recorded.FollowUpEventId!.Value], default));
            await using var wrongDb = fixture.CreateDbContext();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PostgresCaseTimelineStore(wrongDb)
                .GetOfficerEscalationsAsync(readLease, fixture.CaseId, [recorded.FollowUpEventId!.Value], default));
        }
        var timeline = new CaseTimelineQuery(timelineStore, policy, unit);
        var visible = await timeline.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default);
        var assigned = Assert.Single(visible.Timeline!.Entries, item => item.EventId == recorded.FollowUpEventId).OfficerEscalation;
        Assert.NotNull(assigned);
        Assert.Equal(fixture.NewOwnerActorId, assigned.OfficerActorId);
        Assert.Equal(escalationId, assigned.EscalationId);
        Assert.True(assigned.IsCurrent);
        Assert.False(assigned.FollowUpAcknowledged);
        var officer = ActorContext.FromValidatedPrincipal(Principal(fixture.NewOwnerActorId, fixture.AccessEpoch));
        var acknowledged = await ProcessCommandAsync(fixture, officer,
            new AcknowledgeFollowUpCommand(Guid.NewGuid(), 4, approved.FollowUpId, null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, acknowledged.Status);
        var acknowledgedTimeline = await timeline.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default);
        Assert.True(Assert.Single(acknowledgedTimeline.Timeline!.Entries, item => item.EventId == recorded.FollowUpEventId).OfficerEscalation!.FollowUpAcknowledged);
        var transferred = await ProcessCommandAsync(fixture, fixture.CreateActor(),
            new ChangeActionOwnerCommand(Guid.NewGuid(), 5, ActionOwnerKind.FollowUp, approved.FollowUpId,
                fixture.ActorId, "synthetic transfer", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, transferred.Status);
        var historical = await timeline.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default);
        Assert.False(Assert.Single(historical.Timeline!.Entries, item => item.EventId == recorded.FollowUpEventId).OfficerEscalation!.IsCurrent);
        await db.AccessGrantOperations.Where(item => item.GrantGroupId == fixture.GrantGroupId && item.Operation == CaseOperation.ManageEscalation)
            .ExecuteDeleteAsync();
        var redacted = await timeline.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default);
        Assert.Equal(SwiftAid.Contracts.Cases.CaseTimelineAccess.Found, redacted.Access);
        Assert.Null(Assert.Single(redacted.Timeline!.Entries, item => item.EventId == recorded.FollowUpEventId).OfficerEscalation);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261104160000_HealthOfficerEscalationApprovals"));
    }

    private sealed class FixtureOfficerAuth(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}
