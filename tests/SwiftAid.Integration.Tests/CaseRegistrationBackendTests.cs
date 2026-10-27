using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Application.Serialization;
using SwiftAid.Domain.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Registration;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests for the managed-case registration backend (leases, authorization,
/// receipt lookup, denial audit, journal staging). Uses the same disposable loopback
/// <c>swiftaid_intake_test</c> database gate as <see cref="IntakePostgresFactAttribute"/>.
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
        using var keyRing = SyntheticRegistrationKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var persistence = CreatePersistence(db, keyRing.CreateOptions(), provider);
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);

        await using var lease = await unitOfWork.BeginCreateAsync(CreateOfficer("officer-1"), "workspace-1", CancellationToken.None);
        var candidate = await persistence.FindReceiptCandidateCaseIdAsync(lease, "officer-1", commandId, CancellationToken.None);

        Assert.Equal(caseId, candidate);
        await lease.DisposeAsync();
    }

    [IntakePostgresFact]
    public async Task StageAccepted_CreatesProjectionJournalReceiptAndNotifications()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedRegistrationGrantAsync(db);
        var actor = CreateOfficer("officer-1");
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);
        var authorization = new PostgresCaseRegistrationAuthorization(db);
        using var keyRing = SyntheticRegistrationKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var persistence = CreatePersistence(db, keyRing.CreateOptions(), provider);
        var request = CreateRequest("workspace-1", "ZW");
        var caseId = Guid.NewGuid();

        DateTimeOffset recordedAt;
        IReadOnlyList<CaseEvent> events;
        CanonicalCaseRegistrationSnapshot snapshot;
        await using (var lease = await unitOfWork.BeginCreateAsync(actor, "workspace-1", CancellationToken.None))
        {
            var decision = await authorization.AuthorizeNewScopeAsync(actor, lease, request, CancellationToken.None);
            Assert.True(decision.IsAllowed);
            Assert.False(string.IsNullOrWhiteSpace(decision.MatchedGrantGroupId));

            snapshot = CanonicalCaseRegistrationHasher.CreateSnapshot(actor, request);
            recordedAt = (await persistence.GetDatabaseUtcNowAsync(lease, CancellationToken.None)).ToUniversalTime();
            events = CaseAggregate.CreateManagedCase(
                caseId,
                snapshot.CommandId,
                snapshot.WorkspaceId,
                snapshot.Report,
                null,
                recordedAt,
                new ActorCapabilities(actor.ActorId, CaseCapability.AcceptReport));
            var outcome = await persistence.StageAcceptedRegistrationAsync(
                lease,
                new CaseRegistrationAppendRequest(
                    snapshot, decision.MatchedGrantGroupId!, caseId, actor.AccessEpoch, recordedAt, events),
                CancellationToken.None);

            Assert.Equal(CaseRegistrationAppendStatus.Staged, outcome.Status);
            Assert.Equal(events.Select(item => item.EventId), outcome.EventIds);
            Assert.Equal(2, outcome.AggregateVersion);
            Assert.Equal(1, outcome.ReportRevision);
            await lease.CommitAsync(CancellationToken.None);
        }

        var projection = await db.ManagedCases.AsNoTracking().SingleAsync(item => item.CaseId == caseId);
        Assert.Equal("org-zw-1", projection.OrganizationId);
        Assert.Equal("workspace-1", projection.WorkspaceId);
        Assert.Equal("ZW", projection.CountryCode);
        Assert.Equal(CaseKind.Human, projection.Kind);
        Assert.Equal(Sector.HumanHealth, projection.Sector);
        Assert.Equal("standard", projection.SensitivityCode);
        Assert.Equal("officer-1", projection.AccountableOwnerActorId);
        Assert.Equal(1, projection.ResourceVersion);
        Assert.Equal(2, projection.AggregateVersion);
        Assert.Equal(1, projection.ReportRevision);
        Assert.Equal(CaseWorkflow.Reported, projection.Workflow);
        Assert.Equal(ClinicalClassification.Unclassified, projection.Classification);
        Assert.Equal(LaboratoryState.NotRecorded, projection.Laboratory);
        Assert.Equal(ReportedValueState.Unknown, projection.OutcomeState);
        Assert.Null(projection.OutcomeValue);

        var journalRows = await db.CaseEvents.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderBy(item => item.AggregateVersion)
            .ToListAsync();
        Assert.Equal(2, journalRows.Count);
        Assert.Equal("CaseReported", journalRows[0].EventType);
        Assert.Equal("CaseAssigned", journalRows[1].EventType);
        Assert.Equal(1, journalRows[0].AggregateVersion);
        Assert.Equal(2, journalRows[1].AggregateVersion);
        Assert.All(journalRows, row =>
        {
            Assert.NotEqual(Guid.Empty, row.PayloadId);
            Assert.Equal(snapshot.CommandId, row.CommandId);
            Assert.Equal("officer-1", row.ActorId);
            Assert.Equal(1, row.ReportRevision);
            Assert.InRange(row.SchemaVersion, 1, 2);
        });
        Assert.NotEqual(journalRows[0].PayloadId, journalRows[1].PayloadId);

        var payloads = await db.ProtectedEventPayloads.AsNoTracking()
            .Where(item => item.PayloadId == journalRows[0].PayloadId || item.PayloadId == journalRows[1].PayloadId)
            .ToListAsync();
        Assert.Equal(2, payloads.Count);
        Assert.All(payloads, payload =>
        {
            Assert.NotEmpty(payload.ProtectedBytes);
            Assert.Equal(PostgresProtectedCaseEventPayloadStore.CurrentPurposeVersion, payload.ProtectionPurposeVersion);
        });
        Assert.All(payloads, payload => Assert.DoesNotContain(
            "A concern was reported.", Encoding.UTF8.GetString(payload.ProtectedBytes)));

        var outboxRows = await db.DomainOutbox.AsNoTracking()
            .Where(item => item.EventId == journalRows[0].EventId || item.EventId == journalRows[1].EventId)
            .ToListAsync();
        Assert.Equal(2, outboxRows.Count);

        var receipt = await db.CommandReceipts.AsNoTracking()
            .SingleAsync(item => item.ActorId == "officer-1" && item.CommandId == snapshot.CommandId);
        Assert.Equal(caseId, receipt.CaseId);
        Assert.Equal(CanonicalCaseRegistrationHasher.CommandType, receipt.CommandType);
        Assert.Equal(CanonicalCaseRegistrationHasher.CurrentReceiptProtocolVersion, receipt.SchemaVersion);
        Assert.Equal(snapshot.CanonicalPayloadHash.ToArray(), receipt.CanonicalPayloadHash);
        Assert.Equal(1, receipt.FirstAggregateVersion);
        Assert.Equal(2, receipt.LastAggregateVersion);
        Assert.Equal(1, receipt.ReportRevision);

        var notification = await db.NotificationItems.AsNoTracking()
            .SingleAsync(item => item.AcceptedEventId == journalRows[1].EventId);
        Assert.Equal("officer-1", notification.RecipientActorId);
        Assert.Equal(caseId, notification.ResourceId);
        var delivery = await db.NotificationDeliveryOutbox.AsNoTracking()
            .SingleAsync(item => item.NotificationId == notification.NotificationId);
        Assert.Equal(notification.NotificationId, delivery.NotificationId);

        var audit = await db.SecurityAuditEntries.AsNoTracking()
            .SingleAsync(item => item.ResourceId == caseId);
        Assert.Equal("officer-1", audit.ActorId);
        Assert.Equal("CaseRegistration.RegisterManagedCase", audit.Operation);
        Assert.Equal(AccessDecisionCode.Allowed.ToString(), audit.DecisionCode);
        Assert.Equal(7, audit.AccessEpoch);

        // The staged journal must satisfy the strict receipt reader end to end.
        await using (var replayLease = await unitOfWork.BeginExistingCaseReplayAsync(actor, caseId, CancellationToken.None))
        {
            var replayDecision = await authorization.AuthorizeExistingCaseReplayAsync(actor, replayLease, CancellationToken.None);
            Assert.True(replayDecision.IsAllowed);
            var replayed = await persistence.FindAuthorizedReceiptAsync(
                replayLease, actor.ActorId, snapshot.CommandId, CancellationToken.None);
            Assert.NotNull(replayed);
            Assert.Equal(caseId, replayed.CaseId);
            Assert.Equal(snapshot.CanonicalPayloadHash.ToArray(), replayed.CanonicalPayloadHash.ToArray());
            Assert.Equal(events.Select(item => item.EventId), replayed.AcceptedEventIds);
            Assert.Equal(2, replayed.AggregateVersion);
            Assert.Equal(1, replayed.ReportRevision);
            await replayLease.DisposeAsync();
        }
    }

    [IntakePostgresFact]
    public async Task StageAccepted_UnknownGrant_ReturnsAuthorizationChangedWithoutStaging()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedRegistrationIdentitiesAsync(db);
        var actor = CreateOfficer("officer-1");
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);
        using var keyRing = SyntheticRegistrationKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var persistence = CreatePersistence(db, keyRing.CreateOptions(), provider);
        var request = CreateRequest("workspace-1", "ZW");
        var snapshot = CanonicalCaseRegistrationHasher.CreateSnapshot(actor, request);
        var caseId = Guid.NewGuid();

        await using var lease = await unitOfWork.BeginCreateAsync(actor, "workspace-1", CancellationToken.None);
        var recordedAt = (await persistence.GetDatabaseUtcNowAsync(lease, CancellationToken.None)).ToUniversalTime();
        var events = CaseAggregate.CreateManagedCase(
            caseId,
            snapshot.CommandId,
            snapshot.WorkspaceId,
            snapshot.Report,
            null,
            recordedAt,
            new ActorCapabilities(actor.ActorId, CaseCapability.AcceptReport));
        var outcome = await persistence.StageAcceptedRegistrationAsync(
            lease,
            new CaseRegistrationAppendRequest(snapshot, "grant-group-unknown", caseId, actor.AccessEpoch, recordedAt, events),
            CancellationToken.None);

        Assert.Equal(CaseRegistrationAppendStatus.AuthorizationChanged, outcome.Status);
        Assert.Empty(outcome.EventIds);
        Assert.Equal(0, await db.ManagedCases.CountAsync(item => item.CaseId == caseId));
        Assert.Equal(0, await db.CaseEvents.CountAsync(item => item.CaseId == caseId));
        Assert.Equal(0, await db.CommandReceipts.CountAsync(item => item.CommandId == snapshot.CommandId));
        await lease.DisposeAsync();
    }

    [IntakePostgresFact]
    public async Task StageAccepted_RestagedCommandKey_ReturnsCommandKeyConflict()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedRegistrationGrantAsync(db);
        var actor = CreateOfficer("officer-1");
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);
        var authorization = new PostgresCaseRegistrationAuthorization(db);
        using var keyRing = SyntheticRegistrationKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var persistence = CreatePersistence(db, keyRing.CreateOptions(), provider);
        var request = CreateRequest("workspace-1", "ZW");
        var snapshot = CanonicalCaseRegistrationHasher.CreateSnapshot(actor, request);
        var caseId = Guid.NewGuid();

        CaseRegistrationAppendRequest append;
        await using (var lease = await unitOfWork.BeginCreateAsync(actor, "workspace-1", CancellationToken.None))
        {
            var decision = await authorization.AuthorizeNewScopeAsync(actor, lease, request, CancellationToken.None);
            Assert.True(decision.IsAllowed);
            var recordedAt = (await persistence.GetDatabaseUtcNowAsync(lease, CancellationToken.None)).ToUniversalTime();
            var events = CaseAggregate.CreateManagedCase(
                caseId,
                snapshot.CommandId,
                snapshot.WorkspaceId,
                snapshot.Report,
                null,
                recordedAt,
                new ActorCapabilities(actor.ActorId, CaseCapability.AcceptReport));
            append = new CaseRegistrationAppendRequest(
                snapshot, decision.MatchedGrantGroupId!, caseId, actor.AccessEpoch, recordedAt, events);
            var first = await persistence.StageAcceptedRegistrationAsync(lease, append, CancellationToken.None);
            Assert.Equal(CaseRegistrationAppendStatus.Staged, first.Status);
            await lease.CommitAsync(CancellationToken.None);
        }

        await using (var lease = await unitOfWork.BeginCreateAsync(actor, "workspace-1", CancellationToken.None))
        {
            var retry = await persistence.StageAcceptedRegistrationAsync(lease, append, CancellationToken.None);
            Assert.Equal(CaseRegistrationAppendStatus.CommandKeyConflict, retry.Status);
            Assert.Empty(retry.EventIds);
            await lease.DisposeAsync();
        }
    }

    [IntakePostgresFact]
    public async Task StageAccepted_SingleEvent_ThrowsArgument()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedRegistrationGrantAsync(db);
        var actor = CreateOfficer("officer-1");
        var unitOfWork = new PostgresCaseRegistrationUnitOfWork(db);
        var authorization = new PostgresCaseRegistrationAuthorization(db);
        using var keyRing = SyntheticRegistrationKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var persistence = CreatePersistence(db, keyRing.CreateOptions(), provider);
        var request = CreateRequest("workspace-1", "ZW");
        var snapshot = CanonicalCaseRegistrationHasher.CreateSnapshot(actor, request);
        var caseId = Guid.NewGuid();

        await using var lease = await unitOfWork.BeginCreateAsync(actor, "workspace-1", CancellationToken.None);
        var decision = await authorization.AuthorizeNewScopeAsync(actor, lease, request, CancellationToken.None);
        Assert.True(decision.IsAllowed);
        var recordedAt = (await persistence.GetDatabaseUtcNowAsync(lease, CancellationToken.None)).ToUniversalTime();
        var events = CaseAggregate.CreateManagedCase(
            caseId,
            snapshot.CommandId,
            snapshot.WorkspaceId,
            snapshot.Report,
            null,
            recordedAt,
            new ActorCapabilities(actor.ActorId, CaseCapability.AcceptReport));
        var malformed = new CaseRegistrationAppendRequest(
            snapshot, decision.MatchedGrantGroupId!, caseId, actor.AccessEpoch, recordedAt, events.Take(1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            persistence.StageAcceptedRegistrationAsync(lease, malformed, CancellationToken.None).AsTask());
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

    internal static async Task SeedRegistrationIdentitiesAsync(SwiftAidDbContext db)
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

    internal static async Task SeedRegistrationGrantAsync(SwiftAidDbContext db, string grantCountry = "ZW")
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

    private static PostgresCaseRegistrationPersistence CreatePersistence(
        SwiftAidDbContext db,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider provider) =>
        new(
            db,
            new PostgresProtectedCaseEventPayloadStore(
                db, new PostgresCaseCommandUnitOfWork(db), options, provider),
            new CaseEventSerializer());

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

    private sealed class SyntheticRegistrationKeyRing : IDisposable
    {
        private readonly string _rootPath;
        private readonly string _certificatePath;
        private readonly string _certificatePassword;

        private SyntheticRegistrationKeyRing(string rootPath, string keysPath, string certificatePath, string certificatePassword)
        {
            _rootPath = rootPath;
            KeysPath = keysPath;
            _certificatePath = certificatePath;
            _certificatePassword = certificatePassword;
        }

        public string KeysPath { get; }

        public static SyntheticRegistrationKeyRing Create()
        {
            var root = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-registration-staging");
            var keys = Path.Combine(root, "keys");
            var certificatePath = Path.Combine(root, "protection.pfx");
            try
            {
                keys = PrivateKeyRingTestDirectory.CreateKeyDirectory(root);

                var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest(
                    "CN=SwiftAid synthetic registration staging", rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
                var bytes = certificate.Export(X509ContentType.Pfx, password);
                try
                {
                    File.WriteAllBytes(certificatePath, bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }

                return new SyntheticRegistrationKeyRing(root, keys, certificatePath, password);
            }
            catch
            {
                PrivateKeyRingTestDirectory.DeleteRoot(root);
                throw;
            }
        }

        public ProtectedPayloadOptions CreateOptions() => new()
        {
            Enabled = true,
            ApplicationName = "SwiftAid.SyntheticRegistrationStagingTests",
            KeysPath = KeysPath,
            CertificatePath = _certificatePath,
            CertificatePassword = _certificatePassword,
            MaximumPayloadBytes = 1024 * 1024
        };

        public void Dispose() => PrivateKeyRingTestDirectory.DeleteRoot(_rootPath);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SwiftAid.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
