using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Intake;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Registration;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Infrastructure.Authorization;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests for intake promotion execution end to end: a routed guest
/// report promotes through the real <c>IntakeService</c> into a registered managed case
/// with the unique promotion link. Uses the same disposable loopback
/// <c>swiftaid_intake_test</c> database gate as <see cref="IntakePostgresFactAttribute"/>.
/// </summary>
[Collection(IntakePostgresCollection.Name)]
public sealed class IntakePromotionExecutionTests
{
    [IntakePostgresFact]
    public async Task Promote_RoutedHumanReport_CreatesCaseAndLinkEndToEnd()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        using var keyRing = SyntheticPromotionKeyRing.Create();
        var payloadOptions = keyRing.CreateOptions();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            payloadOptions, new TestHostEnvironment(Environments.Production));
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        await SeedPromotionRouteAsync(db);
        var receiptId = await SeedRoutedReportAsync(db, provider);

        var service = CreateService(db, payloadOptions, provider);
        var officer = CreateOfficer("officer-1");
        var result = await service.DecideAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, "Confirmed case."),
            officer,
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.Applied, result.Status);
        Assert.NotNull(result.Receipt);
        Assert.Equal(IntakeStatus.AcceptedIntoCase, result.Receipt.Status);
        Assert.NotNull(result.PromotedCaseId);
        var caseId = result.PromotedCaseId.Value;

        var link = await db.IntakePromotions.AsNoTracking().SingleAsync(item => item.ReceiptId == receiptId);
        Assert.Equal(caseId, link.CaseId);
        Assert.Equal("officer-1", link.AcceptingOfficerActorId);
        Assert.Equal(1, link.SourceRevision);
        Assert.Equal("officer-1", link.InitialAssigneeActorId);

        var report = await db.IntakeReports.AsNoTracking().SingleAsync(item => item.ReceiptId == receiptId);
        Assert.Equal(IntakeStatus.AcceptedIntoCase, report.Status);
        Assert.Equal(2, report.Version);
        Assert.Equal("officer-1", report.DecisionActorId);

        var projection = await db.ManagedCases.AsNoTracking().SingleAsync(item => item.CaseId == caseId);
        Assert.Equal("org-zw-1", projection.OrganizationId);
        Assert.Equal("workspace-1", projection.WorkspaceId);
        Assert.Equal("ZW", projection.CountryCode);
        Assert.Equal(2, projection.AggregateVersion);
        Assert.Equal(2, await db.CaseEvents.CountAsync(item => item.CaseId == caseId));

        var registrationReceipt = await db.CommandReceipts.AsNoTracking().SingleAsync(item =>
            item.ActorId == "officer-1" && item.CommandId == IntakePromotionIds.CommandId(receiptId));
        Assert.Equal(caseId, registrationReceipt.CaseId);

        var replay = await service.DecideAsync(
            new IntakeDecision(receiptId, 2, IntakeDecisionKind.AcceptIntoCase, "Confirmed case."),
            officer,
            CancellationToken.None);
        Assert.Equal(IntakeDecisionStatus.Applied, replay.Status);
        Assert.Equal(caseId, replay.PromotedCaseId);

        var secondOfficer = await service.DecideAsync(
            new IntakeDecision(receiptId, 2, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer("officer-2"),
            CancellationToken.None);
        Assert.Equal(IntakeDecisionStatus.Applied, secondOfficer.Status);
        Assert.Equal(caseId, secondOfficer.PromotedCaseId);

        var stale = await service.DecideAsync(
            new IntakeDecision(receiptId, 1, IntakeDecisionKind.AcceptIntoCase, null),
            officer,
            CancellationToken.None);
        Assert.Equal(IntakeDecisionStatus.Conflict, stale.Status);
    }

    [IntakePostgresFact]
    public async Task Promote_UnknownReceipt_ReturnsNotFound()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        using var keyRing = SyntheticPromotionKeyRing.Create();
        var payloadOptions = keyRing.CreateOptions();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            payloadOptions, new TestHostEnvironment(Environments.Production));

        var service = CreateService(db, payloadOptions, provider);
        var result = await service.DecideAsync(
            new IntakeDecision(Guid.NewGuid(), 1, IntakeDecisionKind.AcceptIntoCase, null),
            CreateOfficer("officer-1"),
            CancellationToken.None);

        Assert.Equal(IntakeDecisionStatus.NotFoundOrNotAuthorized, result.Status);
    }

    private static IntakeService CreateService(
        SwiftAidDbContext db,
        ProtectedPayloadOptions payloadOptions,
        ProtectedPayloadDataProtectionProvider provider)
    {
        var decisions = new PostgresIntakeDecisionStore(db);
        var registration = new CaseRegistrationProcessor(
            new PostgresCaseRegistrationUnitOfWork(db),
            new PostgresCaseRegistrationAuthorization(db),
            new PostgresCaseRegistrationPersistence(
                db,
                new PostgresProtectedCaseEventPayloadStore(
                    db, new PostgresCaseCommandUnitOfWork(db), payloadOptions, provider),
                new CaseEventSerializer()));
        var promotion = new IntakePromotionProcessor(
            new PostgresIntakePromotionSource(db),
            new PostgresIntakeSubmissionOpener(provider),
            new PostgresIntakePromotionWorkspaceResolver(db),
            registration,
            decisions,
            TimeProvider.System);
        return new IntakeService(
            new PostgresIntakeSubmissionStore(db, IntakeLimits.Default, provider),
            decisions,
            promotion,
            new PostgresIntakeAttachmentAuthority(db),
            new UnusedCoverageRouter(),
            new UnusedSubmissionValidator(),
            IntakeLimits.Default,
            TimeProvider.System);
    }

    private static ActorContext CreateOfficer(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private static async Task SeedPromotionRouteAsync(SwiftAidDbContext db)
    {
        db.IntakeRoutes.Add(new IntakeRouteEntity
        {
            RouteId = Guid.NewGuid(),
            CountryCode = "ZW",
            CatalogueVersion = "catalogue-1",
            ReceivingOrganizationId = "org-zw-1",
            IntakeQueueId = "queue-zw-harare",
            EffectiveFromUtc = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAtUtc = null,
            IsActive = true,
            Version = 1
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<Guid> SeedRoutedReportAsync(
        SwiftAidDbContext db, ProtectedPayloadDataProtectionProvider provider)
    {
        var submission = new IntakeSubmission(
            1,
            Guid.NewGuid(),
            IntakeSubjectScope.Human,
            "A concern was reported.",
            ["Fever since morning."],
            [],
            new ReportedTime(DateTimeOffset.UtcNow, null, null, null, "+00:00", null, ReportedTimePrecision.Approximate),
            new ReportedTime(DateTimeOffset.UtcNow.AddHours(-2), null, null, null, "+02:00", null, ReportedTimePrecision.Approximate),
            new LocationContext(
                "ZW",
                new OptionalPlace(null, null, PresenceState.Unknown),
                new OptionalPlace(null, null, PresenceState.Unknown),
                new OptionalPlace(null, null, PresenceState.Unknown),
                null,
                null,
                null,
                LocationSource.Reporter,
                LocationPrecision.Approximate,
                "catalogue-1"),
            new IntakeConfirmations(true, "ZW", "catalogue-1", true, true, "policy-1", "v1", null, null, null));
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(submission, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var protector = provider.CreateProtector("SwiftAid.Intake.Report.v1");
        var protectedPayload = protector.Protect(plaintext);

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
            ClientSubmissionId = submission.ClientSubmissionId,
            CanonicalPayloadDigest = SHA256.HashData(plaintext),
            ProtectedPayload = protectedPayload,
            Status = IntakeStatus.AwaitingReview,
            RoutingStatus = IntakeRoutingStatus.RoutedForReview,
            IntakeQueueId = "queue-zw-harare",
            Version = 1,
            ReceivedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        CryptographicOperations.ZeroMemory(plaintext);
        return receiptId;
    }

    private sealed class UnusedCoverageRouter : ICoverageRouter
    {
        public Task<CoverageResult> RouteAsync(LocationContext location, CaseKind kind, CancellationToken ct) =>
            throw new NotSupportedException("Promotion execution never routes coverage.");
    }

    private sealed class UnusedSubmissionValidator : IIntakeSubmissionValidator
    {
        public Task<bool> ValidateAsync(IntakeSubmission submission, IntakeLimits limits, CancellationToken ct) =>
            throw new NotSupportedException("Promotion execution never validates submissions.");
    }

    private sealed class SyntheticPromotionKeyRing : IDisposable
    {
        private readonly string _rootPath;
        private readonly string _certificatePath;
        private readonly string _certificatePassword;

        private SyntheticPromotionKeyRing(string rootPath, string keysPath, string certificatePath, string certificatePassword)
        {
            _rootPath = rootPath;
            KeysPath = keysPath;
            _certificatePath = certificatePath;
            _certificatePassword = certificatePassword;
        }

        public string KeysPath { get; }

        public static SyntheticPromotionKeyRing Create()
        {
            var root = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-promotion-execution");
            var keys = PrivateKeyRingTestDirectory.CreateKeyDirectory(root);
            var certificatePath = Path.Combine(root, "protection.pfx");
            try
            {
                var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest(
                    "CN=SwiftAid synthetic promotion execution", rsa,
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

                return new SyntheticPromotionKeyRing(root, keys, certificatePath, password);
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
            ApplicationName = "SwiftAid.SyntheticPromotionExecutionTests",
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
