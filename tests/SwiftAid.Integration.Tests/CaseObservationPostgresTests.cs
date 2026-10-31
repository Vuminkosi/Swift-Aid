using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Live PostgreSQL proof that staff-recorded observations flow through the
/// command pipeline into the monitoring projection with their narrative,
/// source, and version intact. Runs only against the disposable loopback
/// fixture database.
/// </summary>
[Collection(CaseAuthorityPostgresCollection.Name)]
public sealed class CaseObservationPostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task RecordObservation_is_accepted_and_projected()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        await using var cleanup = new ObservationCleanup(fixture);
        await AddOperationAsync(fixture, CaseOperation.RecordObservation).ConfigureAwait(false);
        await SetInitialWorkflowVersionAsync(fixture).ConfigureAwait(false);
        using var keyRing = ObservationKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new ObservationTestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider).ConfigureAwait(false);

        var observationId = Guid.NewGuid();
        var command = new RecordObservationCommand(
            Guid.NewGuid(),
            ExpectedVersion: 2,
            ObservationId: observationId,
            Narrative: "synthetic observation narrative",
            Source: "synthetic field visit",
            Reason: "synthetic integration test",
            OccurredAtUtc: null,
            RecordedAtUtc: DateTimeOffset.UtcNow);

        await using var dbContext = fixture.CreateDbContext();
        var result = await ProcessAsync(fixture, keyRing, provider, dbContext, command).ConfigureAwait(false);

        Assert.Equal(CaseCommandProcessingStatus.Accepted, result.Status);
        Assert.Equal(3, result.AggregateVersion);
        var row = await dbContext.ManagedCaseObservations.AsNoTracking()
            .SingleAsync(entity => entity.ObservationId == observationId).ConfigureAwait(false);
        Assert.Equal(fixture.CaseId, row.CaseId);
        Assert.Equal("synthetic observation narrative", row.Narrative);
        Assert.Equal("synthetic field visit", row.Source);
        Assert.Equal(3, row.AggregateVersion);
        var eventRow = await dbContext.CaseEvents.AsNoTracking()
            .SingleAsync(entity => entity.CaseId == fixture.CaseId && entity.AggregateVersion == 3)
            .ConfigureAwait(false);
        Assert.Equal("CaseObservationRecorded", eventRow.EventType);
    }

    [CaseAuthorityPostgresFact]
    public async Task RecordObservation_without_grant_is_denied()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        await using var cleanup = new ObservationCleanup(fixture);
        await SetInitialWorkflowVersionAsync(fixture).ConfigureAwait(false);
        using var keyRing = ObservationKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new ObservationTestHostEnvironment(Environments.Production));
        await SeedInitialJournalAsync(fixture, keyRing.CreateOptions(), provider).ConfigureAwait(false);

        var command = new RecordObservationCommand(
            Guid.NewGuid(),
            ExpectedVersion: 2,
            ObservationId: Guid.NewGuid(),
            Narrative: "synthetic observation narrative",
            Source: "synthetic field visit",
            Reason: "synthetic integration test",
            OccurredAtUtc: null,
            RecordedAtUtc: DateTimeOffset.UtcNow);

        await using var dbContext = fixture.CreateDbContext();
        var result = await ProcessAsync(fixture, keyRing, provider, dbContext, command).ConfigureAwait(false);

        Assert.Equal(CaseCommandProcessingStatus.Denied, result.Status);
        Assert.Empty(await dbContext.ManagedCaseObservations.AsNoTracking()
            .Where(entity => entity.CaseId == fixture.CaseId).ToArrayAsync().ConfigureAwait(false));
    }

    private static async Task<CaseCommandProcessingResult> ProcessAsync(
        CaseAuthorityPostgresFixture fixture,
        ObservationKeyRing keyRing,
        ProtectedPayloadDataProtectionProvider provider,
        SwiftAidDbContext dbContext,
        RecordObservationCommand command)
    {
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var payloadStore = new PostgresProtectedCaseEventPayloadStore(
            dbContext, unitOfWork, keyRing.CreateOptions(), provider);
        var persistence = new PostgresCaseCommandPersistence(
            dbContext, unitOfWork, payloadStore, new CaseEventSerializer());
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(dbContext, unitOfWork),
            new PostgresCaseAccessDescriptorStore(dbContext, unitOfWork));
        var processor = new CaseCommandProcessor(unitOfWork, policy, persistence);
        return await processor.ProcessAsync(
            fixture.CreateActor(), fixture.CaseId, 1, command, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task AddOperationAsync(CaseAuthorityPostgresFixture fixture, CaseOperation operation)
    {
        await using var dbContext = fixture.CreateDbContext();
        dbContext.AccessGrantOperations.Add(new AccessGrantOperationEntity
        {
            GrantGroupId = fixture.GrantGroupId,
            Operation = operation
        });
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task SetInitialWorkflowVersionAsync(CaseAuthorityPostgresFixture fixture)
    {
        await using var dbContext = fixture.CreateDbContext();
        var row = await dbContext.ManagedCases
            .SingleAsync(entity => entity.CaseId == fixture.CaseId).ConfigureAwait(false);
        row.WorkflowVersion = 1;
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task SeedInitialJournalAsync(
        CaseAuthorityPostgresFixture fixture,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider provider)
    {
        var actor = fixture.CreateActor();
        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var store = new PostgresProtectedCaseEventPayloadStore(dbContext, unitOfWork, options, provider);
        var serializer = new CaseEventSerializer();
        await using var lease = await unitOfWork.BeginCaseAsync(
            actor, fixture.CaseId, [], CaseLeaseMode.Write, CancellationToken.None).ConfigureAwait(false);
        var commandId = Guid.NewGuid();
        var recordedAt = DateTimeOffset.UtcNow;
        var report = new ReportFacts(
            CaseKind.Human,
            Sector.HumanHealth,
            "synthetic protected report text",
            Array.AsReadOnly<ReportedObservation>([new ReportedObservation("synthetic observation")]));
        var initialEvents = CaseAggregate.Create(
            new AcceptCaseCommand(
                commandId, fixture.CaseId, report, fixture.ActorId, fixture.WorkspaceId, null, recordedAt),
            new ActorCapabilities(fixture.ActorId, CaseCapability.AcceptReport | CaseCapability.AssignCase));

        dbContext.CommandReceipts.Add(new CommandReceiptEntity
        {
            ReceiptId = Guid.NewGuid(),
            ActorId = fixture.ActorId,
            CommandId = commandId,
            CaseId = fixture.CaseId,
            CommandType = "AcceptCase",
            SchemaVersion = 1,
            CanonicalPayloadHash = SHA256.HashData([]),
            FirstAggregateVersion = 1,
            LastAggregateVersion = 2,
            ReportRevision = 1,
            AcceptedAtUtc = recordedAt
        });

        foreach (var @event in initialEvents)
        {
            var eventType = @event.GetType().Name;
            var serialized = serializer.SerializeToUtf8Bytes(@event);
            try
            {
                var payloadId = Guid.NewGuid();
                var reference = await store.StageProtectedAsync(
                    fixture.CaseId, payloadId, eventType, 1, serialized, CancellationToken.None).ConfigureAwait(false);
                dbContext.CaseEvents.Add(new CaseEventEntity
                {
                    EventId = @event.EventId,
                    CaseId = @event.CaseId,
                    AggregateVersion = @event.AggregateVersion,
                    ReportRevision = @event.ReportRevision,
                    CommandId = @event.CommandId,
                    ActorId = @event.ActorId,
                    EventType = eventType,
                    SchemaVersion = 1,
                    OccurredAtUtc = @event.OccurredAtUtc,
                    RecordedAtUtc = @event.RecordedAtUtc,
                    PayloadId = reference.PayloadId
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serialized);
            }
        }

        await lease.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class ObservationCleanup(CaseAuthorityPostgresFixture fixture) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var dbContext = fixture.CreateDbContext();
            var payloadIds = await dbContext.CaseEvents.AsNoTracking()
                .Where(entity => entity.CaseId == fixture.CaseId)
                .Select(entity => entity.PayloadId)
                .ToArrayAsync().ConfigureAwait(false);
            var eventIds = await dbContext.CaseEvents.AsNoTracking()
                .Where(entity => entity.CaseId == fixture.CaseId)
                .Select(entity => entity.EventId)
                .ToArrayAsync().ConfigureAwait(false);
            await dbContext.DomainOutbox.Where(entity => eventIds.Contains(entity.EventId))
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await dbContext.CaseEvents.Where(entity => entity.CaseId == fixture.CaseId)
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await dbContext.CommandReceipts.Where(entity => entity.CaseId == fixture.CaseId)
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await dbContext.ProtectedEventPayloads.Where(entity => payloadIds.Contains(entity.PayloadId))
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await dbContext.SecurityAuditEntries.Where(entity => entity.ResourceId == fixture.CaseId)
                .ExecuteDeleteAsync().ConfigureAwait(false);
            await dbContext.ManagedCaseObservations.Where(entity => entity.CaseId == fixture.CaseId)
                .ExecuteDeleteAsync().ConfigureAwait(false);
        }
    }

    private sealed class ObservationKeyRing : IDisposable
    {
        private readonly string _rootPath;
        private readonly string _certificatePath;
        private readonly string _certificatePassword;

        private ObservationKeyRing(string rootPath, string keysPath, string certificatePath, string certificatePassword)
        {
            _rootPath = rootPath;
            KeysPath = keysPath;
            _certificatePath = certificatePath;
            _certificatePassword = certificatePassword;
        }

        public string KeysPath { get; }

        public static ObservationKeyRing Create()
        {
            var userDataPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userDataPath) || !Path.IsPathFullyQualified(userDataPath))
            {
                throw new InvalidOperationException("A user-private fixture parent is required for the observation key ring.");
            }

            var root = Path.Combine(userDataPath, $"swiftaid-observation-tests-{Guid.NewGuid():N}");
            var keys = Path.Combine(root, "keys");
            var certificatePath = Path.Combine(root, "protection.pfx");
            try
            {
                CreatePrivateDirectory(root);
                CreatePrivateDirectory(keys);
                var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=SwiftAid synthetic observations", rsa,
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

                return new ObservationKeyRing(root, keys, certificatePath, password);
            }
            catch
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                throw;
            }
        }

        public ProtectedPayloadOptions CreateOptions() => new()
        {
            Enabled = true,
            ApplicationName = "SwiftAid.SyntheticObservationTests",
            KeysPath = KeysPath,
            CertificatePath = _certificatePath,
            CertificatePassword = _certificatePassword,
            MaximumPayloadBytes = 1024 * 1024
        };

        private static void CreatePrivateDirectory(string path)
        {
            if (OperatingSystem.IsLinux())
            {
                Directory.CreateDirectory(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return;
            }

            Directory.CreateDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private sealed class ObservationTestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SwiftAid.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(AppContext.BaseDirectory);
    }
}
