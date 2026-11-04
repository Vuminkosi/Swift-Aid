using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Intake;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests for the PC-09 intake coordination queue: coordinator
/// authorization, redacted queue/detail reads, and versioned audited actions.
/// </summary>
[Collection(IntakePostgresCollection.Name)]
public sealed class IntakeCoordinationTests
{
    private const string CoordinationOrg = "org-coord-1";
    private const string CoordinatorId = "coordinator-1";

    [IntakePostgresFact]
    public async Task Coordinator_IsAuthorizedForUnroutedReport()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        var receiptId = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var authorizer = CreateAuthorizer(db);

        var authority = await authorizer.AuthorizeReportAsync(CreateCoordinator(), receiptId, CancellationToken.None);

        Assert.NotNull(authority);
        Assert.Equal(CoordinatorId, authority.ActorId);
        Assert.Equal(CoordinationOrg, authority.CoordinationOrganizationId);
        Assert.False(string.IsNullOrWhiteSpace(authority.GrantGroupId));
    }

    [IntakePostgresFact]
    public async Task OrganizationReviewer_IsNotCoordinator()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        var receiptId = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var authorizer = CreateAuthorizer(db);

        var authority = await authorizer.AuthorizeReportAsync(
            CreateOfficer("officer-1"), receiptId, CancellationToken.None);

        Assert.Null(authority);
    }

    [IntakePostgresFact]
    public async Task Coordinator_IsDeniedWhenQueueDisabled()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        var receiptId = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var authorizer = new PostgresIntakeCoordinationAuthorizer(
            db, new IntakeCoordinationOptions { Enabled = false, CoordinationOrganizationId = CoordinationOrg });

        var authority = await authorizer.AuthorizeReportAsync(CreateCoordinator(), receiptId, CancellationToken.None);

        Assert.Null(authority);
    }

    [IntakePostgresFact]
    public async Task Coordinator_IsDeniedForRoutedReport()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        var receiptId = await SeedRoutedReportAsync(db);
        var authorizer = CreateAuthorizer(db);

        var authority = await authorizer.AuthorizeReportAsync(CreateCoordinator(), receiptId, CancellationToken.None);

        Assert.Null(authority);
    }

    [IntakePostgresFact]
    public async Task Queue_ListsOnlyUnroutedReportsWithRoutingFields()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        var needsRouting = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting, country: "ZW", region: "harare", area: "budiriro");
        var noCoverage = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NoServiceCoverage, country: "SZ");
        await SeedRoutedReportAsync(db);
        var service = CreateService(db);

        var entries = await service.GetQueueAsync(CreateCoordinator(), null, 200, null, null, CancellationToken.None);

        Assert.Contains(entries, item => item.ReceiptId == needsRouting);
        Assert.Contains(entries, item => item.ReceiptId == noCoverage);
        Assert.DoesNotContain(entries, item => item.IntakeQueueId == "queue-zw-harare");
        var first = entries.Single(item => item.ReceiptId == needsRouting);
        Assert.Equal("ZW", first.CountryCode);
        Assert.Equal("harare", first.RegionId);
        Assert.Equal("budiriro", first.AreaId);
        Assert.Equal(IntakeRoutingStatus.NeedsRouting, first.RoutingStatus);
        Assert.Null(first.IntakeQueueId);
        Assert.Contains(entries, item => item.ReceiptId == noCoverage);
    }

    [IntakePostgresFact]
    public async Task Queue_FiltersByRoutingStatus()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        var needsRouting = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var uncovered = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NoServiceCoverage);
        var service = CreateService(db);

        var entries = await service.GetQueueAsync(
            CreateCoordinator(), IntakeRoutingStatus.NoServiceCoverage, 200, null, null, CancellationToken.None);

        Assert.Contains(entries, item => item.ReceiptId == uncovered);
        Assert.DoesNotContain(entries, item => item.ReceiptId == needsRouting);
    }

    [IntakePostgresFact]
    public async Task Queue_PagesWithCursor()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        // Staggered timestamps fix the relative order of these rows under any shared-DB pollution.
        var baseTime = DateTimeOffset.UtcNow;
        var oldest = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting, receivedAtUtc: baseTime);
        var middle = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting, receivedAtUtc: baseTime.AddMinutes(1));
        var newest = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting, receivedAtUtc: baseTime.AddMinutes(2));
        var service = CreateService(db);
        var coordinator = CreateCoordinator();

        var all = await service.GetQueueAsync(coordinator, null, 200, null, null, CancellationToken.None);
        var positions = all
            .Select((entry, index) => (entry.ReceiptId, index))
            .Where(pair => pair.ReceiptId == newest || pair.ReceiptId == middle || pair.ReceiptId == oldest)
            .OrderBy(pair => pair.index)
            .Select(pair => pair.ReceiptId)
            .ToArray();
        Assert.Equal([newest, middle, oldest], positions);

        var cursor = all.Single(item => item.ReceiptId == middle);
        var after = await service.GetQueueAsync(
            coordinator, null, 200, cursor.ReceivedAtUtc, cursor.ReceiptId, CancellationToken.None);
        // Shared-DB rows from other suites may interleave below the cursor; the cursor
        // contract is that the page starts at or before the oldest seeded row and never
        // repeats rows from above the cursor.
        Assert.Contains(after, item => item.ReceiptId == oldest);
        Assert.DoesNotContain(after, item => item.ReceiptId == newest || item.ReceiptId == middle);
    }

    [IntakePostgresFact]
    public async Task EligibleQueues_ListsActiveRouteQueues()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        await SeedEligibleRouteAsync(db, "queue-zw-harare");
        await SeedEligibleRouteAsync(db, "queue-zw-bulawayo");
        var service = CreateService(db);

        var queues = await service.GetEligibleQueuesAsync(CreateCoordinator(), CancellationToken.None);

        Assert.Contains(queues, item => item.QueueId == "queue-zw-harare" && item.ReceivingOrganizationId == "org-zw-1");
        Assert.Contains(queues, item => item.QueueId == "queue-zw-bulawayo");
    }

    [IntakePostgresFact]
    public async Task NonCoordinator_QueueIsEmpty()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var service = CreateService(db);

        var entries = await service.GetQueueAsync(CreateOfficer("officer-1"), null, 50, null, null, CancellationToken.None);

        Assert.Empty(entries);
    }

    [IntakePostgresFact]
    public async Task Claim_AssignsCoordinatorAndRecordsAudit()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        var receiptId = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var service = CreateService(db);

        var result = await service.ActAsync(
            new IntakeCoordinationCommand(receiptId, 1, IntakeCoordinationAction.ClaimForReview, null, "Taking review."),
            CreateCoordinator(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, result.Status);
        var detail = await service.GetDetailAsync(CreateCoordinator(), receiptId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(CoordinatorId, detail.Report.CoordinatorActorId);
        Assert.Equal(2, detail.Report.Version);
        var action = Assert.Single(detail.History);
        Assert.Equal(IntakeCoordinationAction.ClaimForReview, action.Action);
        Assert.Equal(CoordinatorId, action.ActorId);
        Assert.Equal(CoordinationOrg, action.CoordinationOrganizationId);
        Assert.Equal("Taking review.", action.Reason);
        Assert.Equal(IntakeStatus.ReceivedUnverified, action.FromStatus);
        Assert.Equal(IntakeRoutingStatus.NeedsRouting, action.FromRouting);
        Assert.Equal(2, action.ResultingVersion);
    }

    [IntakePostgresFact]
    public async Task AssignQueue_MovesReportIntoOrganizationReview()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        await SeedEligibleRouteAsync(db, "queue-zw-harare");
        var receiptId = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var service = CreateService(db);

        var result = await service.ActAsync(
            new IntakeCoordinationCommand(receiptId, 1, IntakeCoordinationAction.AssignQueue, "queue-zw-harare", "Catchment confirmed."),
            CreateCoordinator(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, result.Status);
        Assert.Equal(IntakeStatus.AwaitingReview, result.Receipt!.Status);
        Assert.Equal(IntakeRoutingStatus.RoutedForReview, result.Receipt.RoutingStatus);

        // The handed-off report leaves coordinator visibility.
        var authorizer = CreateAuthorizer(db);
        Assert.Null(await authorizer.AuthorizeReportAsync(CreateCoordinator(), receiptId, CancellationToken.None));
        Assert.Null(await service.GetDetailAsync(CreateCoordinator(), receiptId, CancellationToken.None));
    }

    [IntakePostgresFact]
    public async Task AssignQueue_UnknownQueue_IsInvalid()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        var receiptId = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var service = CreateService(db);

        var result = await service.ActAsync(
            new IntakeCoordinationCommand(receiptId, 1, IntakeCoordinationAction.AssignQueue, "queue-unknown", "Guessing."),
            CreateCoordinator(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Invalid, result.Status);
    }

    [IntakePostgresFact]
    public async Task StaleVersion_Conflicts()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        var receiptId = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var service = CreateService(db);
        var coordinator = CreateCoordinator();

        var first = await service.ActAsync(
            new IntakeCoordinationCommand(receiptId, 1, IntakeCoordinationAction.ClaimForReview, null, "Taking review."),
            coordinator, CancellationToken.None);
        Assert.Equal(IntakeDecisionStatus.Applied, first.Status);

        var stale = await service.ActAsync(
            new IntakeCoordinationCommand(receiptId, 1, IntakeCoordinationAction.ClaimForReview, null, "Taking review."),
            coordinator, CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Conflict, stale.Status);
    }

    [IntakePostgresFact]
    public async Task RecordNoCoverage_MarksUncoveredAndReaffirms()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        var receiptId = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var service = CreateService(db);
        var coordinator = CreateCoordinator();

        var marked = await service.ActAsync(
            new IntakeCoordinationCommand(receiptId, 1, IntakeCoordinationAction.RecordNoServiceCoverage, null, "No catchment covers this area."),
            coordinator, CancellationToken.None);
        Assert.Equal(IntakeDecisionStatus.Applied, marked.Status);

        var detail = await service.GetDetailAsync(coordinator, receiptId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(IntakeRoutingStatus.NoServiceCoverage, detail.Report.RoutingStatus);

        var reaffirmed = await service.ActAsync(
            new IntakeCoordinationCommand(receiptId, 2, IntakeCoordinationAction.RecordNoServiceCoverage, null, "Still uncovered."),
            coordinator, CancellationToken.None);
        Assert.Equal(IntakeDecisionStatus.Applied, reaffirmed.Status);
        Assert.Equal(2, (await service.GetDetailAsync(coordinator, receiptId, CancellationToken.None))!.Report.Version);
    }

    [IntakePostgresFact]
    public async Task RequestClarification_SetsFlag()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        var receiptId = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var service = CreateService(db);

        var result = await service.ActAsync(
            new IntakeCoordinationCommand(receiptId, 1, IntakeCoordinationAction.RequestClarification, null, "Need onset date."),
            CreateCoordinator(),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, result.Status);
        var detail = await service.GetDetailAsync(CreateCoordinator(), receiptId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.True(detail.Report.ClarificationRequested);
    }

    [IntakePostgresFact]
    public async Task TerminalReport_IsNotCoordinatorVisible()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedCoordinationGrantAsync(db);
        var receiptId = await SeedUnroutedReportAsync(db, IntakeRoutingStatus.NeedsRouting);
        var service = CreateService(db);
        await service.ActAsync(
            new IntakeCoordinationCommand(receiptId, 1, IntakeCoordinationAction.RecordNoServiceCoverage, null, "Uncovered."),
            CreateCoordinator(), CancellationToken.None);
        await DeclineReportAsync(db, receiptId);

        var authorizer = CreateAuthorizer(db);
        Assert.Null(await authorizer.AuthorizeReportAsync(CreateCoordinator(), receiptId, CancellationToken.None));
        Assert.Null(await service.GetDetailAsync(CreateCoordinator(), receiptId, CancellationToken.None));
        var result = await service.ActAsync(
            new IntakeCoordinationCommand(receiptId, 2, IntakeCoordinationAction.ClaimForReview, null, "Too late."),
            CreateCoordinator(), CancellationToken.None);
        Assert.Equal(IntakeDecisionStatus.NotFoundOrNotAuthorized, result.Status);
    }

    private static ActorContext CreateCoordinator() => CreateOfficer(CoordinatorId);

    private static ActorContext CreateOfficer(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private static IntakeCoordinationOptions CoordinationOptions() => new()
    {
        Enabled = true,
        CoordinationOrganizationId = CoordinationOrg
    };

    private static PostgresIntakeCoordinationAuthorizer CreateAuthorizer(SwiftAidDbContext db) =>
        new(db, CoordinationOptions());

    private static IntakeCoordinationProcessor CreateService(SwiftAidDbContext db) =>
        new(CreateAuthorizer(db), new PostgresIntakeCoordinationStore(db), TimeProvider.System);

    internal static async Task SeedCoordinationGrantAsync(SwiftAidDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        if (!await db.Actors.AsNoTracking().AnyAsync(item => item.ActorId == CoordinatorId))
        {
            db.Actors.Add(new ActorEntity
            {
                ActorId = CoordinatorId,
                Status = PersistentActorStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            db.ActorAccessStates.Add(new ActorAccessStateEntity
            {
                ActorId = CoordinatorId,
                AccessEpoch = 7,
                Version = 1,
                UpdatedAtUtc = now
            });
        }

        if (!await db.Actors.AsNoTracking().AnyAsync(item => item.ActorId == "granter-1"))
        {
            db.Actors.Add(new ActorEntity
            {
                ActorId = "granter-1",
                Status = PersistentActorStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        if (!await db.Organizations.AsNoTracking().AnyAsync(item => item.OrganizationId == CoordinationOrg))
        {
            db.Organizations.Add(new OrganizationEntity
            {
                OrganizationId = CoordinationOrg,
                Status = PersistentResourceStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        if (!await db.Workspaces.AsNoTracking().AnyAsync(item => item.WorkspaceId == "workspace-coord-1"))
        {
            db.Workspaces.Add(new WorkspaceEntity
            {
                WorkspaceId = "workspace-coord-1",
                OrganizationId = CoordinationOrg,
                Status = PersistentResourceStatus.Active
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var grantIds = await db.AccessGrantGroups.AsNoTracking()
            .Where(item => item.ActorId == CoordinatorId)
            .Select(item => item.GrantGroupId)
            .ToArrayAsync();
        if (grantIds.Length > 0)
        {
            await db.AccessGrantOperations
                .Where(item => grantIds.Contains(item.GrantGroupId))
                .ExecuteDeleteAsync();
            await db.AccessGrantGroups
                .Where(item => item.ActorId == CoordinatorId)
                .ExecuteDeleteAsync();
        }

        var membership = await db.OrganizationMemberships
            .SingleOrDefaultAsync(item => item.ActorId == CoordinatorId && item.OrganizationId == CoordinationOrg);
        if (membership is null)
        {
            membership = new OrganizationMembershipEntity
            {
                MembershipId = Guid.NewGuid(),
                ActorId = CoordinatorId,
                OrganizationId = CoordinationOrg,
                Status = PersistentMembershipStatus.Active,
                ValidFromUtc = now.AddHours(-1),
                ExpiresAtUtc = null,
                GrantedByActorId = "granter-1",
                Version = 1
            };
            db.OrganizationMemberships.Add(membership);
        }
        else
        {
            membership.ValidFromUtc = now.AddHours(-1);
            membership.ExpiresAtUtc = null;
            membership.Version += 1;
        }

        var access = await db.ActorAccessStates
            .SingleOrDefaultAsync(item => item.ActorId == CoordinatorId);
        if (access is not null)
        {
            access.AccessEpoch = 7;
            access.Version += 1;
            access.UpdatedAtUtc = now;
        }

        var grantGroupId = $"grant-group-coord-{Guid.NewGuid():N}";
        db.AccessGrantGroups.Add(new AccessGrantGroupEntity
        {
            GrantGroupId = grantGroupId,
            MembershipId = membership.MembershipId,
            ActorId = CoordinatorId,
            OrganizationId = CoordinationOrg,
            WorkspaceId = "workspace-coord-1",
            CountryCode = "ZW",
            Sector = Sector.HumanHealth,
            SensitivityCode = "standard",
            CanAccessJurisdictionalCases = false,
            Status = PersistentGrantStatus.Active,
            ValidFromUtc = now.AddHours(-1),
            ExpiresAtUtc = now.AddHours(1),
            GrantedByActorId = "granter-1",
            Version = 1
        });
        db.AccessGrantOperations.Add(new AccessGrantOperationEntity
        {
            GrantGroupId = grantGroupId,
            Operation = CaseOperation.CoordinateIntake
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<Guid> SeedUnroutedReportAsync(
        SwiftAidDbContext db,
        IntakeRoutingStatus routing,
        string country = "ZW",
        string? region = null,
        string? area = null,
        DateTimeOffset? receivedAtUtc = null)
    {
        var now = DateTimeOffset.UtcNow;
        var receivedAt = receivedAtUtc ?? now;
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
            ProtectedPayload = [0x01],
            Status = IntakeStatus.ReceivedUnverified,
            RoutingStatus = routing,
            IntakeQueueId = null,
            Version = 1,
            ReceivedAtUtc = receivedAt,
            RoutingCountryCode = country,
            RoutingRegionId = region,
            RoutingAreaId = area,
            RoutingCatalogueVersion = "catalogue-1",
            SubjectScope = IntakeSubjectScope.Human
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return receiptId;
    }

    private static async Task<Guid> SeedRoutedReportAsync(SwiftAidDbContext db)
    {
        await CaseRegistrationBackendTests.SeedRegistrationIdentitiesAsync(db);
        await SeedEligibleRouteAsync(db, "queue-zw-harare");
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
            ProtectedPayload = [0x01],
            Status = IntakeStatus.AwaitingReview,
            RoutingStatus = IntakeRoutingStatus.RoutedForReview,
            IntakeQueueId = "queue-zw-harare",
            Version = 1,
            ReceivedAtUtc = now,
            RoutingCountryCode = "ZW",
            RoutingCatalogueVersion = "catalogue-1",
            SubjectScope = IntakeSubjectScope.Human
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return receiptId;
    }

    private static async Task SeedEligibleRouteAsync(SwiftAidDbContext db, string queueId)
    {
        await CaseRegistrationBackendTests.SeedRegistrationIdentitiesAsync(db);
        if (!await db.IntakeRoutes.AsNoTracking().AnyAsync(item => item.IntakeQueueId == queueId))
        {
            db.IntakeRoutes.Add(new IntakeRouteEntity
            {
                RouteId = Guid.NewGuid(),
                CountryCode = "ZW",
                CatalogueVersion = "catalogue-1",
                ReceivingOrganizationId = "org-zw-1",
                IntakeQueueId = queueId,
                EffectiveFromUtc = DateTimeOffset.UtcNow.AddDays(-1),
                ExpiresAtUtc = null,
                IsActive = true,
                Version = 1
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
    }

    private static async Task DeclineReportAsync(SwiftAidDbContext db, Guid receiptId)
    {
        var report = await db.IntakeReports.SingleAsync(item => item.ReceiptId == receiptId);
        report.Status = IntakeStatus.Declined;
        report.Version += 1;
        report.DecidedAtUtc = DateTimeOffset.UtcNow;
        report.DecisionActorId = CoordinatorId;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
