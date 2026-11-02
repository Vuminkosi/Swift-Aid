using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenIddict.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests for the intake decision store. Set
/// <c>SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES=1</c> and point
/// <c>SWIFTAID_TEST_INTAKE_POSTGRES_CONNECTION</c> at a disposable loopback PostgreSQL
/// database named <c>swiftaid_intake_test</c> (plain PostgreSQL 16+; no PostGIS required).
/// Without both settings every test skips; no database claim is made.
/// </summary>
public sealed class IntakePostgresFactAttribute : FactAttribute
{
    public const string RunVariable = "SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES";
    public const string ConnectionVariable = "SWIFTAID_TEST_INTAKE_POSTGRES_CONNECTION";

    public IntakePostgresFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RunVariable), "1", StringComparison.Ordinal))
        {
            Skip = "Set SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES=1 with SWIFTAID_TEST_INTAKE_POSTGRES_CONNECTION " +
                "pointing at a disposable loopback PostgreSQL database to run intake persistence tests.";
        }
    }
}

[Trait("Category", "IntakePostgres")]
[Collection(IntakePostgresCollection.Name)]
public sealed class IntakeDecisionStoreTests
{
    [IntakePostgresFact]
    public async Task Decline_AppliesStatusVersionAndAuditFields()
    {
        await using var db = await CreateMigratedContextAsync();
        var receiptId = await SeedReportAsync(db, IntakeStatus.AwaitingReview, 1);
        var store = new PostgresIntakeDecisionStore(db);

        var result = await store.DecideAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.Decline, "Duplicate of an earlier report."),
            CreateOfficer("officer-1"),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, result.Status);
        Assert.NotNull(result.Receipt);
        Assert.Equal(IntakeStatus.Declined, result.Receipt.Status);
        var stored = await db.IntakeReports.AsNoTracking().SingleAsync(item => item.ReceiptId == receiptId);
        Assert.Equal(2, stored.Version);
        Assert.Equal("officer-1", stored.DecisionActorId);
        Assert.Equal("Duplicate of an earlier report.", stored.ReviewReason);
        Assert.NotNull(stored.DecidedAtUtc);
    }

    [IntakePostgresFact]
    public async Task SameDecision_Replay_ReturnsAppliedWithoutVersionBump()
    {
        await using var db = await CreateMigratedContextAsync();
        var receiptId = await SeedReportAsync(db, IntakeStatus.Declined, 4, decidedBy: "officer-1");
        var store = new PostgresIntakeDecisionStore(db);

        var result = await store.DecideAsync(
            new IntakeDecision(receiptId, 4, IntakeDecisionKind.Decline, "Second look, same outcome."),
            CreateOfficer("officer-2"),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, result.Status);
        Assert.Equal(IntakeStatus.Declined, result.Receipt!.Status);
        var stored = await db.IntakeReports.AsNoTracking().SingleAsync(item => item.ReceiptId == receiptId);
        Assert.Equal(4, stored.Version);
        Assert.Equal("officer-1", stored.DecisionActorId);
    }

    [IntakePostgresFact]
    public async Task StaleExpectedVersion_Conflicts()
    {
        await using var db = await CreateMigratedContextAsync();
        var receiptId = await SeedReportAsync(db, IntakeStatus.AwaitingReview, 2);
        var store = new PostgresIntakeDecisionStore(db);

        var result = await store.DecideAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.Decline, null),
            CreateOfficer("officer-1"),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Conflict, result.Status);
        Assert.Null(result.Receipt);
    }

    [IntakePostgresFact]
    public async Task UnknownReceipt_ReturnsNotFoundOrNotAuthorized()
    {
        await using var db = await CreateMigratedContextAsync();
        var store = new PostgresIntakeDecisionStore(db);

        var result = await store.DecideAsync(
            new IntakeDecision(Guid.NewGuid(), 1, IntakeDecisionKind.Decline, null),
            CreateOfficer("officer-1"),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.NotFoundOrNotAuthorized, result.Status);
        Assert.Null(result.Receipt);
    }

    [IntakePostgresFact]
    public async Task RouteForReview_WithoutRoutedCoverage_IsInvalid()
    {
        await using var db = await CreateMigratedContextAsync();
        var receiptId = await SeedReportAsync(
            db, IntakeStatus.ReceivedUnverified, 1, IntakeRoutingStatus.NeedsRouting, null);
        var store = new PostgresIntakeDecisionStore(db);

        var result = await store.DecideAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.RouteForReview, null),
            CreateOfficer("officer-1"),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Invalid, result.Status);
        Assert.Null(result.Receipt);
    }

    [IntakePostgresFact]
    public async Task LinkAsEvidence_RecordsLinkAtomicallyWithStatus()
    {
        await using var db = await CreateMigratedIntakeContextAsync();
        var receiptId = await SeedReportAsync(db, IntakeStatus.AwaitingReview, 1);
        var caseId = await SeedManagedCaseAsync(db);
        var store = new PostgresIntakeDecisionStore(db);

        var result = await store.DecideAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.LinkAsEvidence, null, caseId),
            CreateOfficer("officer-1"),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, result.Status);
        Assert.Equal(IntakeStatus.LinkedAsEvidence, result.Receipt!.Status);
        var stored = await db.IntakeReports.AsNoTracking().SingleAsync(item => item.ReceiptId == receiptId);
        Assert.Equal(2, stored.Version);
        var link = await db.IntakeEvidenceLinks.AsNoTracking().SingleAsync(item => item.ReceiptId == receiptId);
        Assert.Equal(caseId, link.CaseId);
        Assert.Equal("officer-1", link.LinkingOfficerActorId);
        Assert.Equal(1, link.SourceRevision);
    }

    [IntakePostgresFact]
    public async Task LinkReplay_SameCase_AppliesIdempotently()
    {
        await using var db = await CreateMigratedIntakeContextAsync();
        var receiptId = await SeedReportAsync(db, IntakeStatus.AwaitingReview, 1);
        var caseId = await SeedManagedCaseAsync(db);
        var store = new PostgresIntakeDecisionStore(db);
        var command = new IntakeDecision(receiptId, 1, IntakeDecisionKind.LinkAsEvidence, null, caseId);
        await store.DecideAsync(command, CreateOfficer("officer-1"), DateTimeOffset.UtcNow, CancellationToken.None);

        var replay = await store.DecideAsync(
            command with { ExpectedVersion = 2 },
            CreateOfficer("officer-1"),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, replay.Status);
        var stored = await db.IntakeReports.AsNoTracking().SingleAsync(item => item.ReceiptId == receiptId);
        Assert.Equal(2, stored.Version);
        Assert.Equal(1, await db.IntakeEvidenceLinks.AsNoTracking().CountAsync(item => item.ReceiptId == receiptId));
    }

    [IntakePostgresFact]
    public async Task LinkReplay_DifferentCase_Conflicts()
    {
        await using var db = await CreateMigratedIntakeContextAsync();
        var receiptId = await SeedReportAsync(db, IntakeStatus.AwaitingReview, 1);
        var caseId = await SeedManagedCaseAsync(db);
        var otherCaseId = await SeedManagedCaseAsync(db);
        var store = new PostgresIntakeDecisionStore(db);
        await store.DecideAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.LinkAsEvidence, null, caseId),
            CreateOfficer("officer-1"), DateTimeOffset.UtcNow, CancellationToken.None);

        var result = await store.DecideAsync(
            new IntakeDecision(receiptId, 2, IntakeDecisionKind.LinkAsEvidence, null, otherCaseId),
            CreateOfficer("officer-1"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Conflict, result.Status);
        Assert.Null(result.Receipt);
    }

    [IntakePostgresFact]
    public async Task LinkToUnknownCase_Conflicts()
    {
        await using var db = await CreateMigratedIntakeContextAsync();
        var receiptId = await SeedReportAsync(db, IntakeStatus.AwaitingReview, 1);
        var store = new PostgresIntakeDecisionStore(db);

        var result = await store.DecideAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.LinkAsEvidence, null, Guid.NewGuid()),
            CreateOfficer("officer-1"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Conflict, result.Status);
        Assert.Null(result.Receipt);
    }

    [IntakePostgresFact]
    public async Task AcceptIntoCase_ThrowsPromotionUnavailable()
    {
        await using var db = await CreateMigratedContextAsync();
        var receiptId = await SeedReportAsync(db, IntakeStatus.AwaitingReview, 1);
        var store = new PostgresIntakeDecisionStore(db);

        await Assert.ThrowsAsync<IntakePromotionUnavailableException>(() => store.DecideAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer("officer-1"),
            DateTimeOffset.UtcNow,
            CancellationToken.None));
    }

    private static ActorContext CreateOfficer(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    /// <summary>
    /// Resets the officer's authorization scope to a known state and returns the membership id.
    /// Shared intake suites reuse stable officer identities; without this reset, an earlier
    /// test's membership window, epoch, or grants would leak into later tests through the
    /// shared disposable database.
    /// </summary>
    internal static async Task<Guid> ResetOfficerScopeAsync(
        SwiftAidDbContext db, string officerId, long accessEpoch, bool membershipExpired = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!await db.Actors.AsNoTracking().AnyAsync(item => item.ActorId == officerId))
        {
            db.Actors.Add(new ActorEntity
            {
                ActorId = officerId,
                Status = PersistentActorStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
        }

        db.ChangeTracker.Clear();
        var grantIds = await db.AccessGrantGroups.AsNoTracking()
            .Where(item => item.ActorId == officerId)
            .Select(item => item.GrantGroupId)
            .ToArrayAsync();
        if (grantIds.Length > 0)
        {
            await db.AccessGrantOperations
                .Where(item => grantIds.Contains(item.GrantGroupId))
                .ExecuteDeleteAsync();
            await db.AccessGrantRegions
                .Where(item => grantIds.Contains(item.GrantGroupId))
                .ExecuteDeleteAsync();
            await db.AccessGrantGroups
                .Where(item => item.ActorId == officerId)
                .ExecuteDeleteAsync();
        }

        var access = await db.ActorAccessStates
            .SingleOrDefaultAsync(item => item.ActorId == officerId);
        if (access is null)
        {
            db.ActorAccessStates.Add(new ActorAccessStateEntity
            {
                ActorId = officerId,
                AccessEpoch = accessEpoch,
                Version = 1,
                UpdatedAtUtc = now
            });
        }
        else
        {
            access.AccessEpoch = accessEpoch;
            access.Version += 1;
            access.UpdatedAtUtc = now;
        }

        var membership = await db.OrganizationMemberships
            .SingleOrDefaultAsync(item => item.ActorId == officerId && item.OrganizationId == "org-zw-1");
        if (membership is null)
        {
            // The membership grantor is an independent FK prerequisite. A fresh
            // database may already contain the officer from a case-only fixture.
            if (!await db.Actors.AnyAsync(item => item.ActorId == "granter-1"))
            {
                db.Actors.Add(new ActorEntity
                {
                    ActorId = "granter-1",
                    Status = PersistentActorStatus.Active,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await db.SaveChangesAsync();
            }

            membership = new OrganizationMembershipEntity
            {
                MembershipId = Guid.NewGuid(),
                ActorId = officerId,
                OrganizationId = "org-zw-1",
                Status = PersistentMembershipStatus.Active,
                ValidFromUtc = now.AddHours(-1),
                ExpiresAtUtc = membershipExpired ? now.AddMinutes(-1) : null,
                GrantedByActorId = "granter-1",
                Version = 1
            };
            db.OrganizationMemberships.Add(membership);
        }
        else
        {
            membership.ValidFromUtc = now.AddHours(-1);
            membership.ExpiresAtUtc = membershipExpired ? now.AddMinutes(-1) : null;
            membership.Version += 1;
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return membership.MembershipId;
    }

    internal static async Task<Guid> SeedManagedCaseAsync(SwiftAidDbContext db, string organizationId = "org-zw-1")
    {
        var now = DateTimeOffset.UtcNow;
        if (!await db.Organizations.AsNoTracking().AnyAsync(item => item.OrganizationId == organizationId))
        {
            db.Organizations.Add(new OrganizationEntity
            {
                OrganizationId = organizationId,
                Status = PersistentResourceStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            var workspaceId = organizationId == "org-zw-1" ? "workspace-1" : "workspace-sz-1";
            db.Workspaces.Add(new WorkspaceEntity
            {
                WorkspaceId = workspaceId,
                OrganizationId = organizationId,
                Status = PersistentResourceStatus.Active
            });
        }

        if (!await db.Actors.AsNoTracking().AnyAsync(item => item.ActorId == "officer-1"))
        {
            db.Actors.Add(new ActorEntity
            {
                ActorId = "officer-1",
                Status = PersistentActorStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        var caseId = Guid.NewGuid();
        db.ManagedCases.Add(new ManagedCaseEntity
        {
            CaseId = caseId,
            OrganizationId = organizationId,
            WorkspaceId = organizationId == "org-zw-1" ? "workspace-1" : "workspace-sz-1",
            CountryCode = organizationId == "org-zw-1" ? "ZW" : "SZ",
            Kind = CaseKind.Human,
            Sector = Sector.HumanHealth,
            SensitivityCode = "standard",
            AccountableOwnerActorId = "officer-1",
            ResourceVersion = 1,
            AggregateVersion = 2,
            ReportRevision = 1,
            Workflow = CaseWorkflow.Reported,
            WorkflowVersion = 0,
            Classification = ClinicalClassification.Unclassified,
            ClassificationVersion = 0,
            Laboratory = LaboratoryState.NotRecorded,
            LaboratoryVersion = 0,
            OutcomeState = ReportedValueState.Unknown,
            OutcomeVersion = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return caseId;
    }

    private static async Task<Guid> SeedReportAsync(
        SwiftAidDbContext db,
        IntakeStatus status,
        long version,
        IntakeRoutingStatus routing = IntakeRoutingStatus.RoutedForReview,
        string? queueId = "queue-zw-harare",
        string? decidedBy = null)
    {
        var sessionId = Guid.NewGuid();
        db.IntakeSessions.Add(new IntakeSessionEntity
        {
            SessionId = sessionId,
            ReceiptProofDigest = new byte[32],
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = DateTimeOffset.UtcNow
        });
        var receiptId = Guid.NewGuid();
        db.IntakeReports.Add(new IntakeReportEntity
        {
            ReceiptId = receiptId,
            SessionId = sessionId,
            ClientSubmissionId = Guid.NewGuid(),
            CanonicalPayloadDigest = new byte[32],
            ProtectedPayload = [1],
            Status = status,
            RoutingStatus = routing,
            IntakeQueueId = queueId,
            Version = version,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            DecisionActorId = decidedBy
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return receiptId;
    }

    internal static async Task<SwiftAidDbContext> CreateMigratedIntakeContextAsync() =>
        await CreateMigratedContextAsync();

    private static async Task<SwiftAidDbContext> CreateMigratedContextAsync()
    {
        var configured = Environment.GetEnvironmentVariable(IntakePostgresFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("The disposable intake PostgreSQL connection is required when intake fixtures are enabled.");
        }

        var builder = new NpgsqlConnectionStringBuilder(configured);
        var databaseAllowed = string.Equals(builder.Database, "swiftaid_intake_test", StringComparison.Ordinal) ||
            string.Equals(builder.Database, "swiftaid_test", StringComparison.Ordinal);
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") || !databaseAllowed)
        {
            throw new InvalidOperationException("Intake fixtures accept only a loopback PostgreSQL database named swiftaid_intake_test or swiftaid_test.");
        }

        // The design-time model (and production) applies OpenIddict's entity
        // configuration via UseOpenIddict; the test harness must build the identical
        // model or the migrator reports pending changes and refuses to migrate.
        var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .UseOpenIddict()
            .Options;
        var context = new SwiftAidDbContext(options);
        await context.Database.MigrateAsync();
        return context;
    }
}
