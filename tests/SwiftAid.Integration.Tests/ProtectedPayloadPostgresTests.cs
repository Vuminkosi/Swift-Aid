using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

[Collection(CaseAuthorityPostgresCollection.Name)]
public sealed class ProtectedPayloadPostgresTests
{
    [Fact]
    public void PersistentCookieProviderReadsAuthenticationTicketAfterProviderRecreation()
    {
        using var keyRing = ProtectedPayloadTestKeyRing.Create();
        var options = keyRing.CreateOptions();
        var environment = new TestHostEnvironment(Environments.Production);
        var firstProvider = ProtectedPayloadDataProtectionProviderFactory.Create(options, environment);
        string protectedTicket;

        using (var firstServices = CreateCookieServiceProvider(firstProvider))
        {
            var cookieOptions = firstServices
                .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(IdentityConstants.ApplicationScheme);
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "synthetic-user")],
                IdentityConstants.ApplicationScheme);
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1)
                },
                IdentityConstants.ApplicationScheme);

            protectedTicket = cookieOptions.TicketDataFormat.Protect(ticket);
        }

        firstProvider.Dispose();
        var restartedProvider = ProtectedPayloadDataProtectionProviderFactory.Create(options, environment);
        using var restartedServices = CreateCookieServiceProvider(restartedProvider);
        var restartedCookieOptions = restartedServices
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var restoredTicket = restartedCookieOptions.TicketDataFormat.Unprotect(protectedTicket);

        Assert.NotNull(restoredTicket);
        Assert.Equal("synthetic-user", restoredTicket.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [CaseAuthorityPostgresFact]
    public async Task StagesOnlyCiphertextAndRecreatedProviderOpensPersistedPayload()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        using var keyRing = ProtectedPayloadTestKeyRing.Create();
        var options = keyRing.CreateOptions();
        var environment = new TestHostEnvironment(Environments.Production);
        var firstProvider = ProtectedPayloadDataProtectionProviderFactory.Create(options, environment);
        var firstProviderLifetime = firstProvider as IDisposable;

        try
        {
            await using var dbContext = fixture.CreateDbContext();
            var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
            var actor = fixture.CreateActor();
            await using var lease = await unitOfWork.BeginCaseAsync(
                    actor,
                    fixture.CaseId,
                    ImmutableArray<string>.Empty,
                    CaseLeaseMode.Write,
                    CancellationToken.None)
                .ConfigureAwait(false);

            var payloadId = Guid.NewGuid();
            const string marker = "private fixture payload π";
            var callerBytes = Encoding.UTF8.GetBytes(marker);
            var store = new PostgresProtectedCaseEventPayloadStore(
                dbContext,
                unitOfWork,
                options,
                firstProvider);

            var reference = await store.StageProtectedAsync(
                    fixture.CaseId,
                    payloadId,
                    "CaseReportCorrected",
                    1,
                    callerBytes,
                    CancellationToken.None)
                .ConfigureAwait(false);

            CryptographicOperations.ZeroMemory(callerBytes);
            Assert.Equal(payloadId, reference.PayloadId);
            Assert.Equal(PostgresProtectedCaseEventPayloadStore.CurrentPurposeVersion, reference.ProtectionPurposeVersion);

            var trackedPayload = Assert.Single(dbContext.ProtectedEventPayloads.Local);
            Assert.Equal(payloadId, trackedPayload.PayloadId);
            Assert.Equal(reference.ProtectionPurposeVersion, trackedPayload.ProtectionPurposeVersion);
            Assert.Equal(EntityState.Added, dbContext.Entry(trackedPayload).State);
            Assert.DoesNotContain(marker, Encoding.UTF8.GetString(trackedPayload.ProtectedBytes));

            var commandId = Guid.NewGuid();
            var eventId = Guid.NewGuid();
            dbContext.CommandReceipts.Add(new CommandReceiptEntity
            {
                ReceiptId = Guid.NewGuid(),
                ActorId = actor.ActorId,
                CommandId = commandId,
                CaseId = fixture.CaseId,
                CommandType = "CaseReportCorrected",
                SchemaVersion = 1,
                CanonicalPayloadHash = RandomNumberGenerator.GetBytes(32),
                FirstAggregateVersion = 3,
                LastAggregateVersion = 3,
                ReportRevision = 2,
                AcceptedAtUtc = DateTimeOffset.UtcNow
            });
            dbContext.CaseEvents.Add(new CaseEventEntity
            {
                EventId = eventId,
                CaseId = fixture.CaseId,
                AggregateVersion = 3,
                ReportRevision = 2,
                CommandId = commandId,
                ActorId = actor.ActorId,
                EventType = "CaseReportCorrected",
                SchemaVersion = 1,
                OccurredAtUtc = null,
                RecordedAtUtc = DateTimeOffset.UtcNow,
                PayloadId = payloadId
            });
            await dbContext.SaveChangesAsync().ConfigureAwait(false);

            var keyFiles = Directory.GetFiles(keyRing.KeysPath, "*.xml", SearchOption.TopDirectoryOnly);
            Assert.NotEmpty(keyFiles);
            foreach (var keyFile in keyFiles)
            {
                Assert.DoesNotContain(marker, await File.ReadAllTextAsync(keyFile).ConfigureAwait(false));
            }

            var persistedPayload = await dbContext.ProtectedEventPayloads
                .AsNoTracking()
                .SingleAsync(entity => entity.PayloadId == payloadId)
                .ConfigureAwait(false);
            Assert.DoesNotContain(marker, Encoding.UTF8.GetString(persistedPayload.ProtectedBytes));

            var recreatedProvider = ProtectedPayloadDataProtectionProviderFactory.Create(options, environment);
            try
            {
                var recreatedStore = new PostgresProtectedCaseEventPayloadStore(
                    dbContext,
                    unitOfWork,
                    options,
                    recreatedProvider);
                var opened = await recreatedStore.UnprotectAsync(
                        fixture.CaseId,
                        payloadId,
                        "CaseReportCorrected",
                        1,
                        reference.ProtectionPurposeVersion,
                        trackedPayload.ProtectedBytes,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                Assert.Equal(marker, Encoding.UTF8.GetString(opened));
                CryptographicOperations.ZeroMemory(opened);
            }
            finally
            {
                (recreatedProvider as IDisposable)?.Dispose();
            }

            await lease.DisposeAsync().ConfigureAwait(false);
            await using var checkContext = fixture.CreateDbContext();
            Assert.False(await checkContext.ProtectedEventPayloads
                .AnyAsync(entity => entity.PayloadId == payloadId)
                .ConfigureAwait(false));
            Assert.False(await checkContext.CaseEvents
                .AnyAsync(entity => entity.EventId == eventId)
                .ConfigureAwait(false));
            Assert.False(await checkContext.CommandReceipts
                .AnyAsync(entity => entity.CommandId == commandId && entity.CaseId == fixture.CaseId)
                .ConfigureAwait(false));
        }
        finally
        {
            firstProviderLifetime?.Dispose();
        }
    }

    [CaseAuthorityPostgresFact]
    public async Task RejectsWrongPurposeMetadataAndTamperedCiphertext()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        using var keyRing = ProtectedPayloadTestKeyRing.Create();
        var options = keyRing.CreateOptions();
        var environment = new TestHostEnvironment(Environments.Production);
        var provider = ProtectedPayloadDataProtectionProviderFactory.Create(options, environment);
        var providerLifetime = provider as IDisposable;

        try
        {
            await using var dbContext = fixture.CreateDbContext();
            var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
            var actor = fixture.CreateActor();
            var writeLease = await unitOfWork.BeginCaseAsync(
                    actor,
                    fixture.CaseId,
                    ImmutableArray<string>.Empty,
                    CaseLeaseMode.Write,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var payloadId = Guid.NewGuid();
            var store = new PostgresProtectedCaseEventPayloadStore(dbContext, unitOfWork, options, provider);
            var reference = await store.StageProtectedAsync(
                    fixture.CaseId,
                    payloadId,
                    "CaseActionRequested",
                    1,
                    Encoding.UTF8.GetBytes("do not disclose this payload"),
                    CancellationToken.None)
                .ConfigureAwait(false);
            var protectedBytes = Assert.Single(dbContext.ProtectedEventPayloads.Local).ProtectedBytes.ToArray();

            var commandId = Guid.NewGuid();
            var eventId = Guid.NewGuid();
            dbContext.CommandReceipts.Add(new CommandReceiptEntity
            {
                ReceiptId = Guid.NewGuid(),
                ActorId = actor.ActorId,
                CommandId = commandId,
                CaseId = fixture.CaseId,
                CommandType = "CaseActionRequested",
                SchemaVersion = 1,
                CanonicalPayloadHash = RandomNumberGenerator.GetBytes(32),
                FirstAggregateVersion = 3,
                LastAggregateVersion = 3,
                ReportRevision = 1,
                AcceptedAtUtc = DateTimeOffset.UtcNow
            });
            dbContext.CaseEvents.Add(new CaseEventEntity
            {
                EventId = eventId,
                CaseId = fixture.CaseId,
                AggregateVersion = 3,
                ReportRevision = 1,
                CommandId = commandId,
                ActorId = actor.ActorId,
                EventType = "CaseActionRequested",
                SchemaVersion = 1,
                OccurredAtUtc = null,
                RecordedAtUtc = DateTimeOffset.UtcNow,
                PayloadId = payloadId
            });
            await dbContext.SaveChangesAsync().ConfigureAwait(false);

            await Assert.ThrowsAsync<ProtectedCaseEventPayloadException>(() => store.UnprotectAsync(
                    fixture.CaseId,
                    Guid.NewGuid(),
                    "CaseActionRequested",
                    1,
                    reference.ProtectionPurposeVersion,
                    protectedBytes,
                    CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<NotSupportedException>(() => store.UnprotectAsync(
                    fixture.CaseId,
                    payloadId,
                    "CaseActionRequested",
                    1,
                    "case-event-payload-v999",
                    protectedBytes,
                    CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UnprotectAsync(
                    fixture.SiblingCaseId,
                    payloadId,
                    "CaseActionRequested",
                    1,
                    reference.ProtectionPurposeVersion,
                    protectedBytes,
                    CancellationToken.None).AsTask());

            await dbContext.CaseEvents
                .Where(entity => entity.EventId == eventId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.EventType, "CaseResolved"))
                .ConfigureAwait(false);
            await Assert.ThrowsAsync<ProtectedCaseEventPayloadException>(() => store.UnprotectAsync(
                    fixture.CaseId,
                    payloadId,
                    "CaseResolved",
                    1,
                    reference.ProtectionPurposeVersion,
                    protectedBytes,
                    CancellationToken.None).AsTask());

            await dbContext.CaseEvents
                .Where(entity => entity.EventId == eventId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(entity => entity.EventType, "CaseActionRequested")
                    .SetProperty(entity => entity.SchemaVersion, 2))
                .ConfigureAwait(false);
            await Assert.ThrowsAsync<ProtectedCaseEventPayloadException>(() => store.UnprotectAsync(
                    fixture.CaseId,
                    payloadId,
                    "CaseActionRequested",
                    2,
                    reference.ProtectionPurposeVersion,
                    protectedBytes,
                    CancellationToken.None).AsTask());

            await dbContext.CaseEvents
                .Where(entity => entity.EventId == eventId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.SchemaVersion, 1))
                .ConfigureAwait(false);

            var tampered = protectedBytes.ToArray();
            tampered[0] ^= 0x20;
            await dbContext.ProtectedEventPayloads
                .Where(entity => entity.PayloadId == payloadId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.ProtectedBytes, tampered))
                .ConfigureAwait(false);
            await Assert.ThrowsAsync<ProtectedCaseEventPayloadException>(() => store.UnprotectAsync(
                    fixture.CaseId,
                    payloadId,
                    "CaseActionRequested",
                    1,
                    reference.ProtectionPurposeVersion,
                    tampered,
                    CancellationToken.None).AsTask());

            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(tampered);
            await writeLease.DisposeAsync().ConfigureAwait(false);
            await using var checkContext = fixture.CreateDbContext();
            Assert.False(await checkContext.ProtectedEventPayloads
                .AnyAsync(entity => entity.PayloadId == payloadId)
                .ConfigureAwait(false));
            Assert.False(await checkContext.CaseEvents
                .AnyAsync(entity => entity.EventId == eventId)
                .ConfigureAwait(false));
            Assert.False(await checkContext.CommandReceipts
                .AnyAsync(entity => entity.CommandId == commandId && entity.CaseId == fixture.CaseId)
                .ConfigureAwait(false));
        }
        finally
        {
            providerLifetime?.Dispose();
        }
    }

    [CaseAuthorityPostgresFact]
    public async Task DisabledUnavailableAndReadLeasePathsNeverStageRows()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        await using var dbContext = fixture.CreateDbContext();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(dbContext);
        var actor = fixture.CreateActor();
        var payloadId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("synthetic payload");

        await using (var writeLease = await unitOfWork.BeginCaseAsync(
                         actor,
                         fixture.CaseId,
                         ImmutableArray<string>.Empty,
                         CaseLeaseMode.Write,
                         CancellationToken.None)
                     .ConfigureAwait(false))
        {
            var disabledStore = new PostgresProtectedCaseEventPayloadStore(
                dbContext,
                unitOfWork,
                new ProtectedPayloadOptions(),
                dataProtectionProvider: null);
            await Assert.ThrowsAsync<ProtectedCaseEventPayloadException>(() => disabledStore.StageProtectedAsync(
                    fixture.CaseId,
                    payloadId,
                    "CaseReported",
                    1,
                    bytes,
                    CancellationToken.None).AsTask());
        }

        var enabledOptions = new ProtectedPayloadOptions
        {
            Enabled = true,
            MaximumPayloadBytes = 1024
        };
        await using (var readLease = await unitOfWork.BeginCaseAsync(
                         actor,
                         fixture.CaseId,
                         ImmutableArray<string>.Empty,
                         CaseLeaseMode.Read,
                         CancellationToken.None)
                     .ConfigureAwait(false))
        {
            var readStore = new PostgresProtectedCaseEventPayloadStore(
                dbContext,
                unitOfWork,
                enabledOptions,
                dataProtectionProvider: null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => readStore.StageProtectedAsync(
                    fixture.CaseId,
                    payloadId,
                    "CaseReported",
                    1,
                    bytes,
                    CancellationToken.None).AsTask());
        }

        await using (var writeLease = await unitOfWork.BeginCaseAsync(
                         actor,
                         fixture.CaseId,
                         ImmutableArray<string>.Empty,
                         CaseLeaseMode.Write,
                         CancellationToken.None)
                     .ConfigureAwait(false))
        {
            var unavailableStore = new PostgresProtectedCaseEventPayloadStore(
                dbContext,
                unitOfWork,
                enabledOptions,
                dataProtectionProvider: null);
            await Assert.ThrowsAsync<ProtectedCaseEventPayloadException>(() => unavailableStore.StageProtectedAsync(
                    fixture.CaseId,
                    payloadId,
                    "CaseReported",
                    1,
                    bytes,
                    CancellationToken.None).AsTask());
        }

        Assert.Empty(dbContext.ProtectedEventPayloads.Local);
        CryptographicOperations.ZeroMemory(bytes);
    }

    [Fact]
    public void ValidatorAllowsOnlyExplicitDevelopmentPlaintextKeyRings()
    {
        using var keyRing = ProtectedPayloadTestKeyRing.Create();
        var options = keyRing.CreateOptions();
        var validator = new ProtectedPayloadOptionsValidator(new TestHostEnvironment(Environments.Development));
        options.CertificatePath = string.Empty;
        options.CertificatePassword = string.Empty;
        options.AllowUnencryptedDevelopmentKeys = true;
        Assert.True(validator.Validate(Options.DefaultName, options).Succeeded);

        var productionValidator = new ProtectedPayloadOptionsValidator(new TestHostEnvironment(Environments.Production));
        Assert.True(productionValidator.Validate(Options.DefaultName, options).Failed);
        options.AllowUnencryptedDevelopmentKeys = false;
        Assert.True(productionValidator.Validate(Options.DefaultName, options).Failed);
    }

    [Fact]
    public void ValidatorRequiresExplicitPersistentStorageAndBoundedPayloadSizeWhenEnabled()
    {
        var options = new ProtectedPayloadOptions
        {
            Enabled = true,
            ApplicationName = "SwiftAid.Tests",
            KeysPath = Path.GetTempPath(),
            MaximumPayloadBytes = 1024
        };
        var validator = new ProtectedPayloadOptionsValidator(new TestHostEnvironment(Environments.Production));

        Assert.True(validator.Validate(Options.DefaultName, options).Failed);

        options.MaximumPayloadBytes = 0;
        Assert.True(validator.Validate(Options.DefaultName, options).Failed);
    }

    private sealed class ProtectedPayloadTestKeyRing : IDisposable
    {
        private readonly string _rootPath;

        private ProtectedPayloadTestKeyRing(string rootPath, string keysPath, string certificatePath, string certificatePassword)
        {
            _rootPath = rootPath;
            KeysPath = keysPath;
            CertificatePath = certificatePath;
            CertificatePassword = certificatePassword;
        }

        public string KeysPath { get; }

        private string CertificatePath { get; }

        private string CertificatePassword { get; }

        public static ProtectedPayloadTestKeyRing Create()
        {
            var rootPath = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-protected-payload");
            var keysPath = Path.Combine(rootPath, "keys");
            var certificatePath = Path.Combine(rootPath, "key-protection.pfx");
            try
            {
                keysPath = PrivateKeyRingTestDirectory.CreateKeyDirectory(rootPath);

                var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest(
                    "CN=SwiftAid Synthetic Payload Test",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    DateTimeOffset.UtcNow.AddDays(1));
                var pfxBytes = certificate.Export(X509ContentType.Pfx, password);
                try
                {
                    File.WriteAllBytes(certificatePath, pfxBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pfxBytes);
                }

                return new ProtectedPayloadTestKeyRing(rootPath, keysPath, certificatePath, password);
            }
            catch
            {
                PrivateKeyRingTestDirectory.DeleteRoot(rootPath);
                throw;
            }
        }

        public ProtectedPayloadOptions CreateOptions() => new()
        {
            Enabled = true,
            ApplicationName = "SwiftAid.SyntheticTests",
            KeysPath = KeysPath,
            CertificatePath = CertificatePath,
            CertificatePassword = CertificatePassword,
            MaximumPayloadBytes = 1024 * 1024
        };

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private static ServiceProvider CreateCookieServiceProvider(
        ProtectedPayloadDataProtectionProvider dataProtectionProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme);
        services.AddSingleton(dataProtectionProvider);
        GatewayAuthenticationRegistration.ConfigurePersistentCookieDataProtection(services);
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SwiftAid.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
