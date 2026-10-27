using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests for the intake review authorizer. Uses the same disposable
/// loopback <c>swiftaid_intake_test</c> database gate as <see cref="IntakePostgresFactAttribute"/>.
/// </summary>
[Trait("Category", "IntakePostgres")]
public sealed class IntakeReviewAuthorizerTests
{
    [IntakePostgresFact]
    public async Task ReviewerWithOperationInCatchmentCountry_IsAuthorized()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var receiptId = await SeedAuthorizerScenarioAsync(db);
        var authorizer = new PostgresIntakeReviewAuthorizer(db);

        Assert.True(await authorizer.IsAuthorizedAsync(CreateOfficer("officer-1"), receiptId, CancellationToken.None));
    }

    [IntakePostgresFact]
    public async Task MissingOperation_IsNotAuthorized()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var receiptId = await SeedAuthorizerScenarioAsync(db, operation: CaseOperation.ReadCase);
        var authorizer = new PostgresIntakeReviewAuthorizer(db);

        Assert.False(await authorizer.IsAuthorizedAsync(CreateOfficer("officer-1"), receiptId, CancellationToken.None));
    }

    [IntakePostgresFact]
    public async Task ExpiredMembership_IsNotAuthorized()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var receiptId = await SeedAuthorizerScenarioAsync(db, membershipExpired: true);
        var authorizer = new PostgresIntakeReviewAuthorizer(db);

        Assert.False(await authorizer.IsAuthorizedAsync(CreateOfficer("officer-1"), receiptId, CancellationToken.None));
    }

    [IntakePostgresFact]
    public async Task AccessEpochMismatch_IsNotAuthorized()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var receiptId = await SeedAuthorizerScenarioAsync(db, accessEpoch: 9);
        var authorizer = new PostgresIntakeReviewAuthorizer(db);

        Assert.False(await authorizer.IsAuthorizedAsync(CreateOfficer("officer-1"), receiptId, CancellationToken.None));
    }

    [IntakePostgresFact]
    public async Task GrantForAnotherCountry_IsNotAuthorized()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var receiptId = await SeedAuthorizerScenarioAsync(db, grantCountry: "SZ");
        var authorizer = new PostgresIntakeReviewAuthorizer(db);

        Assert.False(await authorizer.IsAuthorizedAsync(CreateOfficer("officer-1"), receiptId, CancellationToken.None));
    }

    [IntakePostgresFact]
    public async Task UnroutedReport_HasNoAuthorizedReviewer()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var receiptId = await SeedAuthorizerScenarioAsync(db, queueId: null);
        var authorizer = new PostgresIntakeReviewAuthorizer(db);

        Assert.False(await authorizer.IsAuthorizedAsync(CreateOfficer("officer-1"), receiptId, CancellationToken.None));
    }

    private static ActorContext CreateOfficer(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private static async Task<Guid> SeedAuthorizerScenarioAsync(
        SwiftAidDbContext db,
        CaseOperation operation = CaseOperation.ReviewIntake,
        bool membershipExpired = false,
        long accessEpoch = 7,
        string grantCountry = "ZW",
        string? queueId = "queue-zw-harare")
    {
        var now = DateTimeOffset.UtcNow;
        db.Actors.Add(new ActorEntity
        {
            ActorId = "officer-1",
            Status = PersistentActorStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        db.Actors.Add(new ActorEntity
        {
            ActorId = "granter-1",
            Status = PersistentActorStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        db.ActorAccessStates.Add(new ActorAccessStateEntity
        {
            ActorId = "officer-1",
            AccessEpoch = accessEpoch,
            Version = 1,
            UpdatedAtUtc = now
        });
        db.Organizations.Add(new OrganizationEntity
        {
            OrganizationId = "org-zw-1",
            Status = PersistentResourceStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        db.Workspaces.Add(new WorkspaceEntity
        {
            WorkspaceId = "workspace-1",
            OrganizationId = "org-zw-1",
            Status = PersistentResourceStatus.Active
        });
        var membershipId = Guid.NewGuid();
        db.OrganizationMemberships.Add(new OrganizationMembershipEntity
        {
            MembershipId = membershipId,
            ActorId = "officer-1",
            OrganizationId = "org-zw-1",
            Status = PersistentMembershipStatus.Active,
            ValidFromUtc = now.AddHours(-1),
            ExpiresAtUtc = membershipExpired ? now.AddMinutes(-1) : null,
            GrantedByActorId = "granter-1",
            Version = 1
        });
        var grantGroupId = "grant-group-1";
        db.AccessGrantGroups.Add(new AccessGrantGroupEntity
        {
            GrantGroupId = grantGroupId,
            MembershipId = membershipId,
            ActorId = "officer-1",
            OrganizationId = "org-zw-1",
            WorkspaceId = "workspace-1",
            CountryCode = grantCountry,
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
            Operation = operation
        });
        db.IntakeRoutes.Add(new IntakeRouteEntity
        {
            RouteId = Guid.NewGuid(),
            CountryCode = "ZW",
            CatalogueVersion = "m49-2024",
            ReceivingOrganizationId = "org-zw-1",
            IntakeQueueId = "queue-zw-harare",
            EffectiveFromUtc = now.AddHours(-1),
            IsActive = true,
            Version = 1
        });
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
            Status = SwiftAid.Contracts.Intake.IntakeStatus.AwaitingReview,
            RoutingStatus = queueId is null
                ? SwiftAid.Contracts.Intake.IntakeRoutingStatus.NeedsRouting
                : SwiftAid.Contracts.Intake.IntakeRoutingStatus.RoutedForReview,
            IntakeQueueId = queueId,
            Version = 1,
            ReceivedAtUtc = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return receiptId;
    }
}
