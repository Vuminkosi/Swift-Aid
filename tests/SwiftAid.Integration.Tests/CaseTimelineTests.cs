using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests for the authorized managed-case timeline. Uses the same
/// disposable loopback database gate as <see cref="IntakePostgresFactAttribute"/>.
/// </summary>
[Trait("Category", "CaseTimeline")]
[Collection(IntakePostgresCollection.Name)]
public sealed class CaseTimelineTests
{
    [IntakePostgresFact]
    public async Task Assignment_header_is_independent_of_page_and_resets_after_reopening()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        var caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        await SeedRegionScopeAsync(db, caseId);
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        await SeedEventAsync(db, caseId, 1, "CaseReported", now);
        await SeedEventAsync(db, caseId, 2, "CaseAssigned", now.AddMinutes(1));
        var pending = await CreateQuery(db).GetAsync(CreateOfficer("officer-1"), caseId, 1, CancellationToken.None);
        Assert.Equal(2, pending.Timeline!.Header.AssignmentVersion);
        Assert.Null(pending.Timeline.Header.AssignmentAcknowledgedAtUtc);

        await SeedEventAsync(db, caseId, 3, "CaseAssignmentAcknowledged", now.AddMinutes(2));
        await db.ManagedCases.Where(item => item.CaseId == caseId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.AggregateVersion, 3));
        var olderPage = await CreateQuery(db).GetAsync(CreateOfficer("officer-1"), caseId, 1, CancellationToken.None, 3);
        Assert.Equal(2, olderPage.Timeline!.Header.AssignmentVersion);
        Assert.Equal(now.AddMinutes(2), olderPage.Timeline.Header.AssignmentAcknowledgedAtUtc);
        Assert.DoesNotContain(olderPage.Timeline.Entries, item => item.EventType == "CaseAssignmentAcknowledged");

        await SeedEventAsync(db, caseId, 4, "CaseResolved", now.AddMinutes(3));
        await SeedEventAsync(db, caseId, 5, "CaseReopened", now.AddMinutes(4));
        await db.ManagedCases.Where(item => item.CaseId == caseId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.AggregateVersion, 5));
        var reopened = await CreateQuery(db).GetAsync(CreateOfficer("officer-1"), caseId, 1, CancellationToken.None);
        Assert.Equal(5, reopened.Timeline!.Header.AssignmentVersion);
        Assert.Null(reopened.Timeline.Header.AssignmentAcknowledgedAtUtc);
    }

    [IntakePostgresFact]
    public async Task Found_WithRealPolicy_ReturnsOrderedHistoryWithDerivedPromotionFirst()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        var caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        await SeedRegionScopeAsync(db, caseId);
        var receiptId = await SeedPromotionLinkAsync(db, caseId);
        var recordedAt = DateTimeOffset.UtcNow;
        await SeedEventAsync(db, caseId, 3, "CaseResolved", recordedAt.AddMinutes(3));
        await SeedEventAsync(db, caseId, 1, "CaseReported", recordedAt);
        await SeedEventAsync(db, caseId, 2, "CaseAssigned", recordedAt.AddMinutes(1));

        var result = await CreateQuery(db).GetAsync(
            CreateOfficer("officer-1"), caseId, null, CancellationToken.None);

        Assert.Equal(CaseTimelineAccess.Found, result.Access);
        Assert.NotNull(result.Timeline);
        var timeline = result.Timeline!;
        Assert.Equal(caseId, timeline.Header.CaseId);
        Assert.Equal("org-zw-1", timeline.Header.OrganizationId);
        Assert.Equal("workspace-1", timeline.Header.WorkspaceId);
        Assert.Equal("Reported", timeline.Header.Workflow);
        Assert.Equal("officer-1", timeline.Header.AccountableOwnerActorId);
        Assert.Equal(4, timeline.Entries.Count);
        Assert.Equal(CaseTimelineEntryKind.Derived, timeline.Entries[0].Kind);
        Assert.Equal("IntakePromoted", timeline.Entries[0].EventType);
        Assert.Contains(receiptId.ToString("D"), timeline.Entries[0].Label);
        Assert.Equal(
            ["Report accepted", "Accountable owner assigned", "Case resolved"],
            timeline.Entries.Skip(1).Select(entry => entry.Label));
        Assert.Equal([1, 2, 3], timeline.Entries.Skip(1).Select(entry => entry.AggregateVersion));
    }

    [IntakePostgresFact]
    public async Task ForeignCase_ReturnsNotFound()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        var foreignCaseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db, "org-sz-1");

        var result = await CreateQuery(db).GetAsync(
            CreateOfficer("officer-1"), foreignCaseId, null, CancellationToken.None);

        Assert.Equal(CaseTimelineAccess.NotFoundOrNotAuthorized, result.Access);
        Assert.Null(result.Timeline);
    }

    [IntakePostgresFact]
    public async Task UnknownCase_ReturnsNotFound()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);

        var result = await CreateQuery(db).GetAsync(
            CreateOfficer("officer-1"), Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(CaseTimelineAccess.NotFoundOrNotAuthorized, result.Access);
        Assert.Null(result.Timeline);
    }

    [IntakePostgresFact]
    public async Task Limit_RespectsJournalRowCap()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        var caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        await SeedRegionScopeAsync(db, caseId);
        var recordedAt = DateTimeOffset.UtcNow;
        await SeedEventAsync(db, caseId, 1, "CaseReported", recordedAt);
        await SeedEventAsync(db, caseId, 2, "CaseAssigned", recordedAt.AddMinutes(1));
        await SeedEventAsync(db, caseId, 3, "CaseResolved", recordedAt.AddMinutes(2));

        var result = await CreateQuery(db).GetAsync(
            CreateOfficer("officer-1"), caseId, 2, CancellationToken.None);

        Assert.Equal(CaseTimelineAccess.Found, result.Access);
        Assert.NotNull(result.Timeline);
        var entries = result.Timeline!.Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal([2, 3], entries.Select(entry => entry.AggregateVersion));
        Assert.True(result.Timeline.HasOlderEntries);
        Assert.Equal(2, result.Timeline.OlderThanAggregateVersion);
    }

    /// <summary>
    /// The shared case policy requires region coverage: every case region must sit
    /// inside the grant's regions. This helper scopes the officer's current grants
    /// and the case to one shared test region without touching the shared seeds.
    /// </summary>
    private static async Task SeedRegionScopeAsync(SwiftAidDbContext db, Guid caseId)
    {
        const string regionId = "region-harare-1";
        var groupIds = await db.AccessGrantGroups.AsNoTracking()
            .Where(item => item.ActorId == "officer-1")
            .Select(item => item.GrantGroupId)
            .ToArrayAsync();
        foreach (var groupId in groupIds)
        {
            db.AccessGrantRegions.Add(new AccessGrantRegionEntity
            {
                GrantGroupId = groupId,
                RegionId = regionId
            });
        }

        db.ManagedCaseRegions.Add(new ManagedCaseRegionEntity
        {
            CaseId = caseId,
            RegionId = regionId
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static CaseTimelineQuery CreateQuery(SwiftAidDbContext db)
    {
        // Policy stores resolve the ambient case lease through the accessor, so all
        // three collaborators must share one unit of work.
        var unitOfWork = new PostgresCaseCommandUnitOfWork(db);
        return new CaseTimelineQuery(
            new PostgresCaseTimelineStore(db),
            new CaseAccessPolicy(
                new PostgresCurrentAccessGrantStore(db, unitOfWork),
                new PostgresCaseAccessDescriptorStore(db, unitOfWork)),
            unitOfWork);
    }

    private static ActorContext CreateOfficer(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private static async Task SeedEventAsync(
        SwiftAidDbContext db, Guid caseId, long version, string eventType, DateTimeOffset recordedAt)
    {
        var payloadId = Guid.NewGuid();
        db.ProtectedEventPayloads.Add(new ProtectedEventPayloadEntity
        {
            PayloadId = payloadId,
            ProtectedBytes = [1],
            ProtectionPurposeVersion = "timeline-test.v1",
            CreatedAtUtc = recordedAt
        });
        var commandId = Guid.NewGuid();
        db.CommandReceipts.Add(new CommandReceiptEntity
        {
            ReceiptId = Guid.NewGuid(),
            ActorId = "officer-1",
            CommandId = commandId,
            CaseId = caseId,
            CommandType = "TimelineSeed",
            SchemaVersion = 1,
            CanonicalPayloadHash = new byte[32],
            FirstAggregateVersion = version,
            LastAggregateVersion = version,
            ReportRevision = 1,
            AcceptedAtUtc = recordedAt
        });
        db.CaseEvents.Add(new CaseEventEntity
        {
            EventId = Guid.NewGuid(),
            CaseId = caseId,
            AggregateVersion = version,
            ReportRevision = 1,
            CommandId = commandId,
            ActorId = "officer-1",
            EventType = eventType,
            SchemaVersion = 1,
            OccurredAtUtc = recordedAt,
            RecordedAtUtc = recordedAt,
            PayloadId = payloadId
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<Guid> SeedPromotionLinkAsync(SwiftAidDbContext db, Guid caseId)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        db.IntakeSessions.Add(new IntakeSessionEntity
        {
            SessionId = sessionId,
            ReceiptProofDigest = new byte[32],
            CreatedAtUtc = now,
            LastSeenAtUtc = now
        });
        var receiptId = Guid.NewGuid();
        db.IntakeReports.Add(new IntakeReportEntity
        {
            ReceiptId = receiptId,
            SessionId = sessionId,
            ClientSubmissionId = Guid.NewGuid(),
            CanonicalPayloadDigest = new byte[32],
            ProtectedPayload = [1],
            Status = IntakeStatus.AcceptedIntoCase,
            RoutingStatus = IntakeRoutingStatus.RoutedForReview,
            IntakeQueueId = "queue-zw-harare",
            Version = 2,
            ReceivedAtUtc = now
        });
        db.IntakePromotions.Add(new IntakePromotionEntity
        {
            ReceiptId = receiptId,
            CaseId = caseId,
            AcceptingOfficerActorId = "officer-1",
            SourceRevision = 1,
            InitialAssigneeActorId = "officer-1",
            PromotedAtUtc = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return receiptId;
    }
}
