using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Ellai;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseCommandProcessingPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task OfficerAcknowledgementSweepIsDeduplicatedScopedAndBecomesHistorical()
    {
        var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
        await AddOperationsAsync(fixture, [CaseOperation.ManageEscalation, CaseOperation.ManageFollowUp]);
        var targetGrant = await AddEligibleTargetGrantAsync(fixture);
        await SetInitialWorkflowVersionAsync(fixture);
        using var keyRing = SyntheticPayloadKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider);
        var escalation = Guid.NewGuid();
        Assert.Equal(CaseCommandProcessingStatus.Accepted, (await ProcessCommandAsync(fixture, fixture.CreateActor(),
            new RaiseEscalationCommand(Guid.NewGuid(), 2, escalation, "synthetic", [], "synthetic", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider)).Status);
        await using var db = fixture.CreateDbContext();
        await db.AccessGrantGroups.Where(item => item.GrantGroupId == targetGrant)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CanAccessJurisdictionalCases, true));
        db.AccessGrantRegions.Add(new AccessGrantRegionEntity { GrantGroupId = targetGrant, RegionId = fixture.CaseRegionId });
        db.AccessGrantOperations.AddRange(
            new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageEscalation },
            new AccessGrantOperationEntity { GrantGroupId = targetGrant, Operation = CaseOperation.ManageFollowUp });
        await db.SaveChangesAsync();
        var designation = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_officer_designations (designation_id, actor_id, organization_id, country_code, sector, status,
                valid_from_utc, expires_at_utc, designated_by_actor_id, version, created_at_utc, updated_at_utc)
            VALUES ({designation}, {fixture.NewOwnerActorId}, {fixture.OrganizationId}, 'SZ', 'HumanHealth', 'Active',
                clock_timestamp() - interval '1 day', clock_timestamp() + interval '1 day', {fixture.ActorId}, 1, clock_timestamp(), clock_timestamp())
            """);
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
        var officers = new PostgresEligibleHealthOfficerStore(db, unit);
        var settings = new HealthOfficerDirectoryOptions { Enabled = true, EscalationApprovalsEnabled = true, EscalationAssignmentsEnabled = true };
        var approval = new HealthOfficerEscalationApprovalRequest(Guid.NewGuid(), Guid.NewGuid(), fixture.CaseId, escalation, 3,
            fixture.NewOwnerActorId, designation, 1, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(6), DateTimeOffset.UtcNow.AddDays(1));
        var approve = new PostgresHealthOfficerEscalationApprovalService(db, unit, policy, officers, settings);
        Assert.Equal(HealthOfficerEscalationApprovalStatus.Recorded, (await approve.ApproveAsync(fixture.CreateActor(), approval, default)).Status);
        var payload = new PostgresProtectedCaseEventPayloadStore(db, unit, keyRing.CreateOptions(), provider);
        var assign = new PostgresHealthOfficerEscalationAssignmentService(db, unit, policy,
            new PostgresCaseCommandPersistence(db, unit, payload, new CaseEventSerializer()), officers, settings);
        Assert.Equal(HealthOfficerEscalationAssignmentStatus.Recorded, (await assign.AssignAsync(fixture.CreateActor(),
            new HealthOfficerEscalationAssignmentRequest(fixture.CaseId, approval.ApprovalId, Guid.NewGuid()), default)).Status);
        var schedule = new PostgresEllaiOperationalScheduleStore(db);
        await schedule.CreateAsync(fixture.WorkspaceId, "ops.followup", 4, "policy/1", DateTimeOffset.UtcNow.AddMinutes(-1), default);
        var lease = await schedule.TryClaimAsync(fixture.WorkspaceId, "ops.followup", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        var worker = new EllaiFollowUpWork(db, Options.Create(new EllaiFollowUpOptions { Enabled = true, Organizations = [fixture.OrganizationId] }));
        Assert.Equal(EllaiFencedWorkOutcome.NoChange, await worker.RunOneAsync(lease, Guid.NewGuid(), Guid.NewGuid(), default));
        var delay = approval.AcknowledgementDueUtc - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(300);
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
        Assert.Equal(EllaiFencedWorkOutcome.StaleLease, await worker.RunOneAsync(lease with { FencingToken = lease.FencingToken + 1 }, Guid.NewGuid(), Guid.NewGuid(), default));
        await using var replicaDb = fixture.CreateDbContext();
        var replica = new EllaiFollowUpWork(replicaDb, Options.Create(new EllaiFollowUpOptions { Enabled = true, Organizations = [fixture.OrganizationId] }));
        var outcomes = await Task.WhenAll(worker.RunOneAsync(lease, Guid.NewGuid(), Guid.NewGuid(), default),
            replica.RunOneAsync(lease, Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Single(outcomes, outcome => outcome == EllaiFencedWorkOutcome.Committed);
        Assert.Single(outcomes, outcome => outcome == EllaiFencedWorkOutcome.NoChange);
        Assert.Equal(EllaiFencedWorkOutcome.NoChange, await worker.RunOneAsync(lease, Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM ellai_follow_up_recommendations WHERE case_id = {fixture.CaseId}").SingleAsync());
        Assert.False(await db.NotificationItems.AnyAsync(item => item.ResourceId == fixture.CaseId));
        var query = new CaseTimelineQuery(new PostgresCaseTimelineStore(db), policy, unit);
        var result = await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default);
        var activity = Assert.Single(result.Timeline!.Entries.SelectMany(entry => entry.AgentActivities));
        Assert.Equal("OfficerAcknowledgementDue", activity.RecommendationKind);
        Assert.True(activity.FollowUpIsCurrent);
        Assert.Equal("Overdue officer acknowledgment review recommended", activity.Label);
        var reviewer = new PostgresFollowUpRecommendationReviewService(db, unit, policy);
        await db.AccessGrantOperations.Where(item => item.GrantGroupId == fixture.GrantGroupId && item.Operation == CaseOperation.ManageEscalation).ExecuteDeleteAsync();
        Assert.Empty((await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default)).Timeline!.Entries.SelectMany(entry => entry.AgentActivities));
        Assert.Equal(FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized,
            await reviewer.ReviewAsync(fixture.CreateActor(), fixture.CaseId, activity.ActivityId, Guid.NewGuid(), default));
        await AddOperationAsync(fixture, CaseOperation.ManageEscalation);
        var commandId = Guid.NewGuid();
        Assert.Equal(FollowUpRecommendationReviewOutcome.Recorded, await reviewer.ReviewAsync(fixture.CreateActor(), fixture.CaseId, activity.ActivityId, commandId, default));
        Assert.Equal(FollowUpRecommendationReviewOutcome.Replayed, await reviewer.ReviewAsync(fixture.CreateActor(), fixture.CaseId, activity.ActivityId, commandId, default));
        await db.AccessGrantOperations.Where(item => item.GrantGroupId == fixture.GrantGroupId && item.Operation == CaseOperation.ManageEscalation).ExecuteDeleteAsync();
        Assert.Equal(FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized,
            await reviewer.ReviewAsync(fixture.CreateActor(), fixture.CaseId, activity.ActivityId, commandId, default));
        await AddOperationAsync(fixture, CaseOperation.ManageEscalation);
        var officer = ActorContext.FromValidatedPrincipal(Principal(fixture.NewOwnerActorId, fixture.AccessEpoch));
        Assert.Equal(CaseCommandProcessingStatus.Accepted, (await ProcessCommandAsync(fixture, officer,
            new AcknowledgeFollowUpCommand(Guid.NewGuid(), 4, approval.FollowUpId, null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider)).Status);
        var historical = await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default);
        Assert.False(Assert.Single(historical.Timeline!.Entries.SelectMany(entry => entry.AgentActivities)).FollowUpIsCurrent);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, (await ProcessCommandAsync(fixture, officer,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 5, approval.FollowUpId, FollowUpOutcome.Rescheduled,
                "synthetic", DateTimeOffset.UtcNow.AddDays(2), "synthetic", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider)).Status);
        var rescheduled = await query.GetAsync(fixture.CreateActor(), fixture.CaseId, 20, default);
        var assignment = Assert.Single(rescheduled.Timeline!.Entries, entry => entry.OfficerEscalation is not null).OfficerEscalation!;
        Assert.True(assignment.FollowUpAcknowledged);
        Assert.False(Assert.Single(rescheduled.Timeline.Entries.SelectMany(entry => entry.AgentActivities)).FollowUpIsCurrent);
        Assert.Equal(EllaiFencedWorkOutcome.NoChange, await worker.RunOneAsync(lease, Guid.NewGuid(), Guid.NewGuid(), default));
    }
}
