using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Domain.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Registration;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests for the managed-case registration backend (leases, authorization,
/// receipt lookup, denial audit). Uses the same disposable loopback <c>swiftaid_intake_test</c>
/// database gate as <see cref="IntakePostgresFactAttribute"/>. Journal staging is explicitly
/// out of scope until the staging slice lands.
/// </summary>
[Trait("Category", "IntakePostgres")]
public sealed class CaseRegistrationBackendTests
{
    [IntakePostgresFact]
    public async Task CreateLease_CommitsAndDeactivates()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await using (var lease = await unitOfWork.BeginCreateAsync(CreateOfficer("officer-1"), "workspace-1", CancellationToken.None))
        {
            Assert.True(lease.IsActive);
            Assert.Null(lease.CaseId);
            Assert.Equal(CaseRegistrationLeaseMode.Create, lease.Mode);
            await lease.CommitAsync(CancellationToken.None);
            Assert.False(lease.IsActive);
        }
    }

    [IntakePostgresFact]
    public async Task SecondLeaseWhileActive_Throws()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await using (var lease = await unitOfWork.BeginCreateAsync(CreateOfficer("officer-1"), "workspace-1", CancellationToken.None))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                unitOfWork.BeginCreateAsync(CreateOfficer("officer-1"), "workspace-1", CancellationToken.None).AsTask());
        }
    }

    [IntakePostgresFact]
    public async Task ReplayLease_MissingCase_ThrowsKeyNotFound()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            unitOfWork.BeginExistingCaseReplayAsync(CreateOfficer("officer-1"), Guid.NewGuid(), CancellationToken.None).AsTask());
    }

    [IntakePostgresFact]
    public async Task ReplayLease_ExistingCase_BindsCase()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await using (var lease = await unitOfWork.BeginExistingCaseReplayAsync(CreateOfficer("officer-1"), caseId, CancellationToken.None))
        {
            Assert.Equal(caseId, lease.CaseId);
            Assert.Equal(CaseRegistrationLeaseMode.ExistingCaseReplay, lease.Mode);
            await lease.CommitAsync(CancellationToken.None);
        }
    }

    [IntakePostgresFact]
    public async Task AuthorizeNewScope_MatchingGrant_AllowsWithGroup()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedRegistrationGrantAsync(db);
        var authorization = new PostgresCaseRegistrationAuthorization(db);
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await using var lease = await unitOfWork.BeginCreateAsync(CreateOfficer("officer-1"), "workspace-1", CancellationToken.None);
        var decision = await authorization.AuthorizeNewScopeAsync(
            CreateOfficer("officer-1"), lease, CreateRequest("workspace-1", "ZW"), CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.Equal(AccessDecisionCode.Allowed, decision.Code);
        Assert.False(string.IsNullOrWhiteSpace(decision.MatchedGrantGroupId));
        await lease.DisposeAsync();
    }

    [IntakePostgresFact]
    public async Task AuthorizeNewScope_ForeignCountry_Denies()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedRegistrationGrantAsync(db, grantCountry: "SZ");
        var authorization = new PostgresCaseRegistrationAuthorization(db);
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await using var lease = await unitOfWork.BeginCreateAsync(CreateOfficer("officer-1"), "workspace-1", CancellationToken.None);
        var decision = await authorization.AuthorizeNewScopeAsync(
            CreateOfficer("officer-1"), lease, CreateRequest("workspace-1", "ZW"), CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessDecisionCode.CountryOutsideGrant, decision.Code);
        await lease.DisposeAsync();
    }

    [IntakePostgresFact]
    public async Task AuthorizeNewScope_NoGrant_DeniesMissing()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedRegistrationIdentitiesAsync(db);
        var authorization = new PostgresCaseRegistrationAuthorization(db);
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await using var lease = await unitOfWork.BeginCreateAsync(CreateOfficer("officer-1"), "workspace-1", CancellationToken.None);
        var decision = await authorization.AuthorizeNewScopeAsync(
            CreateOfficer("officer-1"), lease, CreateRequest("workspace-1", "ZW"), CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessDecisionCode.GrantMissing, decision.Code);
        await lease.DisposeAsync();
    }

    [IntakePostgresFact]
    public async Task AuthorizeReplay_SameOrg_Allows()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedRegistrationGrantAsync(db);
        var caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        var authorization = new PostgresCaseRegistrationAuthorization(db);
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await using var lease = await unitOfWork.BeginExistingCaseReplayAsync(CreateOfficer("officer-1"), caseId, CancellationToken.None);
        var decision = await authorization.AuthorizeExistingCaseReplayAsync(CreateOfficer("officer-1"), lease, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(decision.MatchedGrantGroupId));
        await lease.DisposeAsync();
    }

    [IntakePostgresFact]
    public async Task AuthorizeReplay_ForeignOrg_Denies()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedRegistrationGrantAsync(db);
        var foreignCaseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db, "org-sz-1");
        var authorization = new PostgresCaseRegistrationAuthorization(db);
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await using var lease = await unitOfWork.BeginExistingCaseReplayAsync(CreateOfficer("officer-1"), foreignCaseId, CancellationToken.None);
        var decision = await authorization.AuthorizeExistingCaseReplayAsync(CreateOfficer("officer-1"), lease, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessDecisionCode.OrganizationOutsideGrant, decision.Code);
        await lease.DisposeAsync();
    }

    [IntakePostgresFact]
    public async Task ReceiptCandidate_LookupFindsStoredReceipt()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        var commandId = Guid.NewGuid();
        await SeedReceiptAsync(db, "officer-1", commandId, caseId);
        var persistence = new PostgresCaseRegistrationPersistence(db);
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await using var lease = await unitOfWork.BeginCreateAsync(CreateOfficer("officer-1"), "workspace-1", CancellationToken.None);
        var candidate = await persistence.FindReceiptCandidateCaseIdAsync(lease, "officer-1", commandId, CancellationToken.None);

        Assert.Equal(caseId, candidate);
        await lease.DisposeAsync();
    }

    [IntakePostgresFact]
    public async Task Staging_ThrowsStagingUnavailable()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var persistence = new PostgresCaseRegistrationPersistence(db);
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await using var lease = await unitOfWork.BeginCreateAsync(CreateOfficer("officer-1"), "workspace-1", CancellationToken.None);
        await Assert.ThrowsAsync<CaseRegistrationStagingUnavailableException>(() =>
            persistence.StageAcceptedRegistrationAsync(lease, null!, CancellationToken.None).AsTask());
        await lease.DisposeAsync();
    }

    private static ActorContext CreateOfficer(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private static RegisterManagedCaseRequest CreateRequest(string workspaceId, string countryCode)
    {
        var source = new ReportProvenance(ReportSourceKind.ReporterObservation, "observation-1");
        var report = HumanReport.Create(
            Guid.Parse("b9e56881-9d31-4691-b91f-21520e729759"),
            new ReportContext(
                ReportedFact<ReportedTime>.Unknown(source),
                null,
                null,
                null,
                ReportedTimePrecision.Unknown,
                new LocationContext(
                    countryCode,
                    new OptionalPlace(null, null, PresenceState.Unknown),
                    new OptionalPlace(null, null, PresenceState.Unknown),
                    new OptionalPlace(null, null, PresenceState.Unknown),
                    null,
                    null,
                    null,
                    LocationSource.Reporter,
                    LocationPrecision.Unknown,
                    null)),
            ReportedFact<string>.Unknown(source),
            ReportedFact<int>.Unknown(source),
            ReportedFact<string>.Unknown(source),
            new ReportEvidence(new ReportedText("A concern was reported.", new ReportProvenance(ReportSourceKind.ReporterNarrative, "narrative-1")), [], null));
        var confirmations = new ReportConfirmations(true, countryCode, "catalogue-1", true, true, "policy-1", "v1", null, null, null);
        return new RegisterManagedCaseRequest(Guid.NewGuid(), workspaceId, new CaseReportEventData.HumanV2(report, confirmations));
    }

    private static async Task SeedRegistrationIdentitiesAsync(SwiftAidDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        if (!await db.Actors.AsNoTracking().AnyAsync(item => item.ActorId == "officer-1"))
        {
            db.Actors.Add(new ActorEntity
            {
                ActorId = "officer-1",
                Status = PersistentActorStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            db.ActorAccessStates.Add(new ActorAccessStateEntity
            {
                ActorId = "officer-1",
                AccessEpoch = 7,
                Version = 1,
                UpdatedAtUtc = now
            });
        }

        if (!await db.Organizations.AsNoTracking().AnyAsync(item => item.OrganizationId == "org-zw-1"))
        {
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
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SeedRegistrationGrantAsync(SwiftAidDbContext db, string grantCountry = "ZW")
    {
        await SeedRegistrationIdentitiesAsync(db);
        var now = DateTimeOffset.UtcNow;
        var membership = await db.OrganizationMemberships
            .SingleOrDefaultAsync(item => item.ActorId == "officer-1" && item.OrganizationId == "org-zw-1");
        if (membership is null)
        {
            membership = new OrganizationMembershipEntity
            {
                MembershipId = Guid.NewGuid(),
                ActorId = "officer-1",
                OrganizationId = "org-zw-1",
                Status = PersistentMembershipStatus.Active,
                ValidFromUtc = now.AddHours(-1),
                GrantedByActorId = "officer-1",
                Version = 1
            };
            db.OrganizationMemberships.Add(membership);
            await db.SaveChangesAsync();
        }

        var grantGroupId = $"grant-group-reg-{Guid.NewGuid():N}";
        db.AccessGrantGroups.Add(new AccessGrantGroupEntity
        {
            GrantGroupId = grantGroupId,
            MembershipId = membership.MembershipId,
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
            GrantedByActorId = "officer-1",
            Version = 1
        });
        db.AccessGrantOperations.Add(new AccessGrantOperationEntity
        {
            GrantGroupId = grantGroupId,
            Operation = CaseOperation.ReadCase
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SeedReceiptAsync(SwiftAidDbContext db, string actorId, Guid commandId, Guid caseId)
    {
        var now = DateTimeOffset.UtcNow;
        db.CommandReceipts.Add(new CommandReceiptEntity
        {
            ReceiptId = Guid.NewGuid(),
            ActorId = actorId,
            CommandId = commandId,
            CaseId = caseId,
            CommandType = CanonicalCaseRegistrationHasher.CommandType,
            SchemaVersion = CanonicalCaseRegistrationHasher.CurrentReceiptProtocolVersion,
            CanonicalPayloadHash = new byte[32],
            FirstAggregateVersion = 1,
            LastAggregateVersion = 2,
            ReportRevision = 1,
            AcceptedAtUtc = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
