using System.Text.Json;
using Google.Apis.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Abstractions;
using SwiftAid.Infrastructure.Configuration;

namespace SwiftAid.Integration.Tests;

public sealed class ProviderConfigurationTests
{
    [Fact]
    public async Task DisabledProvidersReturnExplicitNotConfiguredResults()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwiftAidInfrastructure(new ConfigurationBuilder().Build());

        using var serviceProvider = services.BuildServiceProvider();

        var storageResult = await serviceProvider.GetRequiredService<IObjectStorage>()
            .StoreAsync(new MemoryStream([1, 2, 3]), "application/octet-stream", CancellationToken.None);
        var cacheResult = await serviceProvider.GetRequiredService<ICacheStore>()
            .GetAsync("derived:test", CancellationToken.None);
        var notificationResult = await serviceProvider.GetRequiredService<IInAppNotificationPublisher>()
            .PublishAsync(null!, CancellationToken.None);
        var pushResult = await serviceProvider.GetRequiredService<IPushNotificationSender>()
            .SendAsync(null!, CancellationToken.None);

        Assert.Equal(ObjectStorageStatus.NotConfigured, storageResult.Status);
        Assert.Equal(CacheReadStatus.NotConfigured, cacheResult.Status);
        Assert.Equal(InAppNotificationPublishStatus.NotConfigured, notificationResult.Status);
        Assert.Equal(PushNotificationStatus.NotConfigured, pushResult.Status);
        Assert.Empty(new MinioOptions().AccessKey);
        Assert.Empty(new MinioOptions().SecretKey);
        Assert.Empty(new RedisOptions().ConnectionString);
        Assert.Empty(new NatsOptions().Password);
        Assert.Empty(new FcmOptions().CredentialsFilePath);
        Assert.Empty(new MetaProviderOptions().ApiKey);
        Assert.Empty(new FederationOptions().ClientSecret);
    }

    [Fact]
    public void EnabledMinioRequiresEndpointCredentialsAndAnExplicitSizeLimit()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Minio:Enabled"] = "true",
            ["Minio:Endpoint"] = "https://minio:9000",
            ["Minio:Bucket"] = "private-files",
            ["Minio:AccessKey"] = "local-user",
            ["Minio:SecretKey"] = "local-secret",
            ["Minio:UseSsl"] = "true"
        });

        var services = new ServiceCollection();
        services.AddLogging();

        var exception = Assert.Throws<OptionsValidationException>(
            () => services.AddSwiftAidInfrastructure(configuration));

        Assert.Contains("MaxObjectSizeBytes", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("local-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedisWritesRequireTtlWithinTheConfiguredMaximum()
    {
        var cache = new RecordingDistributedCache();
        var store = new SwiftAid.Infrastructure.Caching.RedisCacheStore(
            cache,
            Options.Create(new RedisOptions { Enabled = true, MaximumTtlSeconds = 7200 }),
            NullLogger<SwiftAid.Infrastructure.Caching.RedisCacheStore>.Instance);

        var accepted = await store.SetAsync(
            "derived:summary",
            new byte[] { 1 },
            TimeSpan.FromSeconds(7200),
            CancellationToken.None);
        var rejected = await store.SetAsync(
            "derived:summary",
            new byte[] { 2 },
            TimeSpan.FromSeconds(7201),
            CancellationToken.None);
        var missingTtl = await store.SetAsync(
            "derived:summary",
            new byte[] { 3 },
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal(CacheWriteStatus.Written, accepted.Status);
        Assert.Equal(TimeSpan.FromSeconds(7200), cache.LastOptions?.AbsoluteExpirationRelativeToNow);
        Assert.Equal(CacheWriteStatus.Failed, rejected.Status);
        Assert.Equal(CacheWriteStatus.Failed, missingTtl.Status);
        Assert.Equal(1, cache.WriteCount);
    }

    [Fact]
    public void RedisMaximumTtlCanBeConfiguredAboveTheDefaultWithinItsSupportedRange()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Redis:Enabled"] = "true",
            ["Redis:ConnectionString"] = "localhost:6379,ssl=true",
            ["Redis:InstanceName"] = "test:",
            ["Redis:MaximumTtlSeconds"] = "7200"
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwiftAidInfrastructure(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        Assert.Equal(
            7200,
            serviceProvider.GetRequiredService<IOptions<RedisOptions>>().Value.MaximumTtlSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86401)]
    [InlineData(long.MaxValue)]
    public void RedisMaximumTtlValidationRejectsValuesOutsideTheSupportedRange(long maximumTtlSeconds)
    {
        var validation = new ProviderOptionsValidator().Validate(
            Options.DefaultName,
            new RedisOptions { MaximumTtlSeconds = maximumTtlSeconds });

        Assert.True(validation.Failed);
        Assert.Contains("between 1 and 86400", string.Join(" ", validation.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledCredentialedProvidersAcceptSecureTransportSyntax()
    {
        var validator = new ProviderOptionsValidator(CreateHostEnvironment(Environments.Production));

        Assert.True(validator.Validate(Options.DefaultName, SecureMinioOptions()).Succeeded);
        Assert.True(validator.Validate(Options.DefaultName, new QdrantOptions
        {
            Enabled = true,
            Endpoint = "https://qdrant.example:6333",
            ApiKey = "synthetic-qdrant-key"
        }).Succeeded);
        Assert.True(validator.Validate(Options.DefaultName, SecureNatsOptions()).Succeeded);
        Assert.True(validator.Validate(Options.DefaultName, new RedisOptions
        {
            Enabled = true,
            ConnectionString = "redis.example:6379,ssl=true,password=synthetic-redis-password",
            InstanceName = "test:",
            MaximumTtlSeconds = 3600
        }).Succeeded);

        var mismatchedMinioTls = SecureMinioOptions();
        mismatchedMinioTls.UseSsl = false;
        Assert.True(validator.Validate(Options.DefaultName, mismatchedMinioTls).Failed);
    }

    [Fact]
    public void AllowlistedPlaintextIsAcceptedOnlyForDevelopmentAndExactHosts()
    {
        var transportSecurity = new TransportSecurityOptions
        {
            AllowPlaintextDevelopment = true,
            AllowedDevelopmentHosts = ["localhost", "10.10.0.5"]
        };
        var validator = new ProviderOptionsValidator(
            CreateHostEnvironment(Environments.Development),
            transportSecurity);

        Assert.True(validator.Validate(Options.DefaultName, PlaintextMinioOptions("http://localhost:9000")).Succeeded);
        Assert.True(validator.Validate(Options.DefaultName, new QdrantOptions
        {
            Enabled = true,
            Endpoint = "http://10.10.0.5:6333",
            ApiKey = "synthetic-qdrant-key"
        }).Succeeded);
        Assert.True(validator.Validate(Options.DefaultName, PlaintextNatsOptions("nats://localhost:4222")).Succeeded);
        Assert.True(validator.Validate(Options.DefaultName, new RedisOptions
        {
            Enabled = true,
            ConnectionString = "localhost:6379,password=synthetic-redis-password",
            InstanceName = "test:",
            MaximumTtlSeconds = 3600
        }).Succeeded);

        var offListResult = validator.Validate(Options.DefaultName, new QdrantOptions
        {
            Enabled = true,
            Endpoint = "http://qdrant.internal:6333",
            ApiKey = "synthetic-qdrant-key"
        });

        Assert.True(offListResult.Failed);

        var credentialsInUrlResult = validator.Validate(
            Options.DefaultName,
            PlaintextNatsOptions("nats://user:synthetic-url-secret@localhost:4222"));

        Assert.True(credentialsInUrlResult.Failed);
        Assert.DoesNotContain(
            "synthetic-url-secret",
            string.Join(" ", credentialsInUrlResult.Failures),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlaintextCannotBeEnabledWithoutDevelopmentEnvironmentAndExplicitFlag()
    {
        var productionValidator = new ProviderOptionsValidator(
            CreateHostEnvironment(Environments.Production),
            new TransportSecurityOptions
            {
                AllowPlaintextDevelopment = true,
                AllowedDevelopmentHosts = ["storage.example"]
            });
        var productionResult = productionValidator.Validate(
            Options.DefaultName,
            PlaintextMinioOptions("http://storage.example:9000"));

        Assert.True(productionResult.Failed);
        Assert.DoesNotContain("synthetic-minio-secret", string.Join(" ", productionResult.Failures), StringComparison.Ordinal);
        Assert.DoesNotContain("storage.example", string.Join(" ", productionResult.Failures), StringComparison.Ordinal);

        var developmentWithoutFlag = new ProviderOptionsValidator(
            CreateHostEnvironment(Environments.Development),
            new TransportSecurityOptions());
        var developmentResult = developmentWithoutFlag.Validate(
            Options.DefaultName,
            PlaintextMinioOptions("http://localhost:9000"));

        Assert.True(developmentResult.Failed);
    }

    [Fact]
    public void DisabledProvidersDoNotRequireTransportSettingsOrCredentials()
    {
        var validator = new ProviderOptionsValidator();

        Assert.True(validator.Validate(Options.DefaultName, new MinioOptions()).Succeeded);
        Assert.True(validator.Validate(Options.DefaultName, new QdrantOptions()).Succeeded);
        Assert.True(validator.Validate(Options.DefaultName, new NatsOptions()).Succeeded);
        Assert.True(validator.Validate(Options.DefaultName, new RedisOptions()).Succeeded);
    }

    [Fact]
    public void InfrastructureRegistrationUsesTheInjectedDevelopmentEnvironment()
    {
        var values = new Dictionary<string, string?>
        {
            ["TransportSecurity:AllowPlaintextDevelopment"] = "true",
            ["TransportSecurity:AllowedDevelopmentHosts:0"] = "localhost",
            ["Minio:Enabled"] = "true",
            ["Minio:Endpoint"] = "http://localhost:9000",
            ["Minio:Bucket"] = "private-files",
            ["Minio:MaxObjectSizeBytes"] = "1024",
            ["Minio:AccessKey"] = "synthetic-minio-key",
            ["Minio:SecretKey"] = "synthetic-minio-secret",
            ["Minio:UseSsl"] = "false"
        };
        var configuration = Configuration(values);
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSwiftAidInfrastructure(configuration, CreateHostEnvironment(Environments.Development));

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IObjectStorage));
    }

    [Fact]
    public void MissingHostEnvironmentFailsClosedForPlaintextProviders()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["TransportSecurity:AllowPlaintextDevelopment"] = "true",
            ["TransportSecurity:AllowedDevelopmentHosts:0"] = "localhost",
            ["Minio:Enabled"] = "true",
            ["Minio:Endpoint"] = "http://localhost:9000",
            ["Minio:Bucket"] = "private-files",
            ["Minio:MaxObjectSizeBytes"] = "1024",
            ["Minio:AccessKey"] = "synthetic-minio-key",
            ["Minio:SecretKey"] = "synthetic-minio-secret",
            ["Minio:UseSsl"] = "false"
        });
        var services = new ServiceCollection();
        services.AddLogging();

        var exception = Assert.Throws<OptionsValidationException>(
            () => services.AddSwiftAidInfrastructure(configuration));

        Assert.Contains("Minio:Endpoint", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-minio-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalSettingsBindWithoutRenamingConnectionStringSections()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SwiftAid"] = "Host=localhost;Database=swiftaid;Username=app;Password=",
            ["Nats:EventStream"] = "SWIFTAID_EVENTS",
            ["Nats:EventSubjectPrefix"] = "swiftaid.events.case",
            ["Nats:NotificationStream"] = "SWIFTAID_NOTIFICATIONS",
            ["Nats:NotificationSubject"] = "swiftaid.notifications.inapp"
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwiftAidInfrastructure(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var database = serviceProvider.GetRequiredService<IOptions<SwiftAidDatabaseOptions>>().Value;
        var nats = serviceProvider.GetRequiredService<IOptions<NatsOptions>>().Value;

        Assert.Contains("Database=swiftaid", database.ConnectionString, StringComparison.Ordinal);
        Assert.Equal("SWIFTAID_EVENTS", nats.EventStream);
        Assert.Equal("swiftaid.events.case", nats.EventSubjectPrefix);
        Assert.Empty(nats.Password);
    }

    [Fact]
    public async Task FcmRegistrationTokenUsesTheTokenWireFieldNotTheFidField()
    {
        const string registrationToken = "synthetic-registration-token";
        var request = new PushNotificationRequest(
            registrationToken,
            PushNotificationKind.CaseUpdateAvailable,
            Guid.NewGuid());
        var message = new FirebaseAdmin.Messaging.Message
        {
#pragma warning disable CS0618 // This test confirms the approved registration-token contract and SDK wire name.
            Token = request.DeviceToken
#pragma warning restore CS0618
        };

        var payload = NewtonsoftJsonSerializer.Instance.Serialize(message);
        using var document = JsonDocument.Parse(payload);

        Assert.Equal(registrationToken, document.RootElement.GetProperty("token").GetString());
        Assert.False(document.RootElement.TryGetProperty("fid", out _));
    }

    [Theory]
    [InlineData("Minio:Enabled", "enabled-ish")]
    [InlineData("Minio:MaxObjectSizeBytes", "10MiB")]
    public void MalformedTypedProviderSettingsFailWithoutEchoingTheirValues(string key, string value)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            [key] = value
        });
        var services = new ServiceCollection();
        services.AddLogging();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddSwiftAidInfrastructure(configuration));

        Assert.Contains("Minio:", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledFcmRequiresServerSideProjectAndCredentialConfiguration()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Fcm:Enabled"] = "true",
            ["Fcm:ProjectId"] = "synthetic-project",
            ["Fcm:CredentialsFilePath"] = "private-service-account.json"
        });
        var services = new ServiceCollection();
        services.AddLogging();

        var exception = Assert.Throws<OptionsValidationException>(
            () => services.AddSwiftAidInfrastructure(configuration));

        Assert.Contains("FCM requires a project ID", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-service-account.json", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static IHostEnvironment CreateHostEnvironment(string environmentName) =>
        new TestHostEnvironment(environmentName);

    private static MinioOptions SecureMinioOptions() => new()
    {
        Enabled = true,
        Endpoint = "https://minio.example:9000",
        Bucket = "private-files",
        MaxObjectSizeBytes = 1024,
        AccessKey = "synthetic-minio-key",
        SecretKey = "synthetic-minio-secret",
        UseSsl = true
    };

    private static MinioOptions PlaintextMinioOptions(string endpoint) => new()
    {
        Enabled = true,
        Endpoint = endpoint,
        Bucket = "private-files",
        MaxObjectSizeBytes = 1024,
        AccessKey = "synthetic-minio-key",
        SecretKey = "synthetic-minio-secret",
        UseSsl = false
    };

    private static NatsOptions SecureNatsOptions() => new()
    {
        Enabled = true,
        Url = "tls://nats.example:4222",
        Username = "synthetic-user",
        Password = "synthetic-nats-password",
        EventStream = "SWIFTAID_EVENTS",
        EventSubjectPrefix = "swiftaid.events.case",
        NotificationStream = "SWIFTAID_NOTIFICATIONS",
        NotificationSubject = "swiftaid.notifications.inapp"
    };

    private static NatsOptions PlaintextNatsOptions(string endpoint) => new()
    {
        Enabled = true,
        Url = endpoint,
        Username = "synthetic-user",
        Password = "synthetic-nats-password",
        EventStream = "SWIFTAID_EVENTS",
        EventSubjectPrefix = "swiftaid.events.case",
        NotificationStream = "SWIFTAID_NOTIFICATIONS",
        NotificationSubject = "swiftaid.notifications.inapp"
    };

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SwiftAid.Integration.Tests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingDistributedCache : IDistributedCache
    {
        public DistributedCacheEntryOptions? LastOptions { get; private set; }

        public int WriteCount { get; private set; }

        public byte[]? Get(string key) => null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key)
        {
        }

        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            LastOptions = options;
            WriteCount++;
        }

        public Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            LastOptions = options;
            WriteCount++;
            return Task.CompletedTask;
        }
    }
}
