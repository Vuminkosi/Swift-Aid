using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
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

    private static async Task<SwiftAidDbContext> CreateMigratedContextAsync()
    {
        var configured = Environment.GetEnvironmentVariable(IntakePostgresFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("The disposable intake PostgreSQL connection is required when intake fixtures are enabled.");
        }

        var builder = new NpgsqlConnectionStringBuilder(configured);
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") ||
            !string.Equals(builder.Database, "swiftaid_intake_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Intake fixtures accept only a loopback PostgreSQL database named swiftaid_intake_test.");
        }

        var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;
        var context = new SwiftAidDbContext(options);
        await context.Database.MigrateAsync();
        return context;
    }
}
