using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using NATS.Client.Core;
using NATS.Client.JetStream.Models;
using NATS.Net;
using SwiftAid.Application.Abstractions;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Events;
using SwiftAid.Infrastructure.Configuration;
using Xunit.Sdk;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in tests against disposable local services. Set SWIFTAID_RUN_LOCAL_PROVIDER_FIXTURES=1
/// and run this class explicitly; ordinary integration runs do not require Docker.
/// </summary>
public sealed class ProviderAdapterContainerTests : IClassFixture<ProviderServicesFixture>
{
    private readonly ProviderServicesFixture _fixture;

    public ProviderAdapterContainerTests(ProviderServicesFixture fixture) => _fixture = fixture;

    [ProviderFixtureFact]
    public async Task MinioAdapterStoresAndCopiesPrivateOpaqueObjectsAndEnforcesSizeLimit()
    {
        await using var services = _fixture.CreateServiceProvider();
        var storage = services.GetRequiredService<IObjectStorage>();
        var content = Enumerable.Range(0, 777).Select(value => (byte)(value % 251)).ToArray();
        using var nonSeekableContent = new NonSeekableReadStream(content);
        Assert.False(nonSeekableContent.CanSeek);

        var stored = await storage.StoreAsync(
            nonSeekableContent,
            "application/octet-stream",
            CancellationToken.None);

        Assert.Equal(ObjectStorageStatus.Succeeded, stored.Status);
        Assert.NotNull(stored.Reference);
        Assert.NotEqual(Guid.Empty, stored.Reference!.Id);
        Assert.Null(stored.FailureCode);

        using var copied = new MemoryStream();
        var copyResult = await storage.CopyToAsync(stored.Reference, copied, CancellationToken.None);

        Assert.Equal(ObjectStorageStatus.Succeeded, copyResult.Status);
        Assert.Equal(content, copied.ToArray());

        using var overLimitContent = new NonSeekableReadStream(
            new byte[ProviderServicesFixture.MaximumObjectBytes + 1]);
        Assert.False(overLimitContent.CanSeek);
        var tooLarge = await storage.StoreAsync(
            overLimitContent,
            "application/octet-stream",
            CancellationToken.None);

        Assert.Equal(ObjectStorageStatus.Failed, tooLarge.Status);
        Assert.Equal("object-too-large", tooLarge.FailureCode);
        Assert.Null(tooLarge.Reference);

        // The fixture uses the MinIO root test credential, so this checks only anonymous read denial;
        // it does not establish least-privilege permissions for an application identity.
        using var anonymousClient = new HttpClient();
        using var anonymousResponse = await anonymousClient.GetAsync(
            $"{_fixture.MinioEndpoint}/{_fixture.Bucket}/{stored.Reference.Id:N}");

        Assert.Equal(HttpStatusCode.Forbidden, anonymousResponse.StatusCode);
    }

    [ProviderFixtureFact]
    public async Task RedisAdapterReadsWritesExpiresAndRemovesDerivedCacheEntries()
    {
        await using var services = _fixture.CreateServiceProvider();
        var cache = services.GetRequiredService<ICacheStore>();
        const string key = "derived:integration-check";
        var payload = new byte[] { 2, 3, 5, 7 };

        var initial = await cache.GetAsync(key, CancellationToken.None);
        Assert.Equal(CacheReadStatus.Miss, initial.Status);

        var write = await cache.SetAsync(key, payload, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(CacheWriteStatus.Written, write.Status);

        var read = await cache.GetAsync(key, CancellationToken.None);
        Assert.Equal(CacheReadStatus.Hit, read.Status);
        Assert.Equal(payload, read.Value);

        var invalidTtl = await cache.SetAsync(key, payload, TimeSpan.FromSeconds(3601), CancellationToken.None);
        Assert.Equal(CacheWriteStatus.Failed, invalidTtl.Status);
        Assert.Equal("invalid-key-or-ttl", invalidTtl.FailureCode);

        var remove = await cache.RemoveAsync(key, CancellationToken.None);
        Assert.Equal(CacheWriteStatus.Removed, remove.Status);
        Assert.Equal(CacheReadStatus.Miss, (await cache.GetAsync(key, CancellationToken.None)).Status);

        const string expiringKey = "derived:expiration-check";
        Assert.Equal(
            CacheWriteStatus.Written,
            (await cache.SetAsync(expiringKey, payload, TimeSpan.FromSeconds(1), CancellationToken.None)).Status);
        await Task.Delay(TimeSpan.FromMilliseconds(1400));
        Assert.Equal(CacheReadStatus.Miss, (await cache.GetAsync(expiringKey, CancellationToken.None)).Status);
    }

    [ProviderFixtureFact]
    public async Task JetStreamAdaptersPublishOnlyToConfiguredStreamsAndReportStorageAcknowledgement()
    {
        await using var services = _fixture.CreateServiceProvider();
        var publisher = services.GetRequiredService<IEventPublisher>();
        var notificationPublisher = services.GetRequiredService<IInAppNotificationPublisher>();
        var now = DateTimeOffset.UtcNow;
        var eventEnvelope = new CaseEventEnvelope<CaseAssignedPayload>(
            SchemaVersion: 1,
            EventId: Guid.NewGuid(),
            EventType: CaseEventType.CaseAssigned,
            CaseId: Guid.NewGuid(),
            AggregateVersion: 1,
            ReportRevision: 1,
            CommandId: Guid.NewGuid(),
            ActorId: "synthetic-actor",
            OccurredAtUtc: now,
            RecordedAtUtc: now,
            Payload: new CaseAssignedPayload("synthetic-owner"));

        var published = await publisher.PublishAsync(eventEnvelope, CancellationToken.None);

        Assert.Equal(EventPublishStatus.AcknowledgedByJetStream, published.Status);
        Assert.Equal(ProviderServicesFixture.EventStream, published.StreamName);
        Assert.True(published.StreamSequence is > 0);

        var notification = new InAppNotification(
            Guid.NewGuid(),
            "synthetic-recipient",
            InAppNotificationKind.CaseUpdateAvailable,
            eventEnvelope.EventId,
            now);
        var notificationResult = await notificationPublisher.PublishAsync(notification, CancellationToken.None);

        Assert.Equal(InAppNotificationPublishStatus.AcknowledgedByJetStream, notificationResult.Status);

        await using var missingStreamServices = _fixture.CreateServiceProvider(
            eventStream: "MISSING_SWIFTAID_TEST_STREAM");
        var missingStreamResult = await missingStreamServices.GetRequiredService<IEventPublisher>()
            .PublishAsync(eventEnvelope with { EventId = Guid.NewGuid(), CommandId = Guid.NewGuid() }, CancellationToken.None);

        Assert.Equal(EventPublishStatus.Unavailable, missingStreamResult.Status);
        Assert.Equal("jetstream-unavailable", missingStreamResult.FailureCode);
    }
}

internal sealed class NonSeekableReadStream(ReadOnlyMemory<byte> content) : Stream
{
    private readonly MemoryStream _inner = new(content.ToArray(), writable: false);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

public sealed class ProviderFixtureFactAttribute : FactAttribute
{
    public const string OptInEnvironmentVariable = "SWIFTAID_RUN_LOCAL_PROVIDER_FIXTURES";

    public ProviderFixtureFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set SWIFTAID_RUN_LOCAL_PROVIDER_FIXTURES=1 to run tests against disposable local provider containers.";
        }
    }
}

public sealed class ProviderServicesFixture : IAsyncLifetime
{
    public const long MaximumObjectBytes = 1024;
    public const string EventStream = "SWIFTAID_TEST_EVENTS";
    public const string NotificationStream = "SWIFTAID_NOTIFICATIONS";

    private const string MinioImage = "swiftaid/minio:RELEASE.2025-10-15T17-29-55Z-source";
    private const string RedisImage = "redis:8.4.7-alpine3.22";
    private const string NatsImage = "nats:2.15.1-alpine3.22";
    private const string FixtureLabel = "com.swiftaid.fixture=provider-adapter-test";
    private const string FixtureIdLabel = "com.swiftaid.fixture-id";
    private readonly List<ContainerIdentity> _containers = [];
    private readonly string _minioUser = $"test-{Guid.NewGuid():N}";
    private readonly string _minioPassword = Guid.NewGuid().ToString("N");
    private readonly string _redisPassword = Guid.NewGuid().ToString("N");
    private readonly string _natsUser = $"test-{Guid.NewGuid():N}";
    private readonly string _natsPassword = Guid.NewGuid().ToString("N");

    public string Bucket { get; } = $"swiftaid-test-{Guid.NewGuid():N}";
    public string MinioEndpoint { get; private set; } = string.Empty;
    public int MinioConsolePort { get; private set; }
    public int RedisPort { get; private set; }
    public int NatsPort { get; private set; }
    private string _minioReadinessStatus = "not-checked";

    public async Task InitializeAsync()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(ProviderFixtureFactAttribute.OptInEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await EnsureDockerAvailableAsync();

            var minioId = await StartContainerAsync(
                MinioImage,
                ["--publish", "127.0.0.1::9000", "--publish", "127.0.0.1::9001",
                    "--env", $"MINIO_ROOT_USER={_minioUser}",
                    "--env", $"MINIO_ROOT_PASSWORD={_minioPassword}"],
                ["server", "/data", "--console-address", ":9001"]);
            var minioApiPort = await GetPublishedPortAsync(minioId, 9000);
            MinioEndpoint = $"http://127.0.0.1:{minioApiPort}";
            MinioConsolePort = await GetPublishedPortAsync(minioId, 9001);
            await WaitForMinioHttpReadyAsync();
            await CreatePrivateBucketAsync();

            var redisId = await StartContainerAsync(
                RedisImage,
                ["--publish", "127.0.0.1::6379", "--entrypoint", "redis-server"],
                ["--appendonly", "no", "--requirepass", _redisPassword]);
            RedisPort = await GetPublishedPortAsync(redisId, 6379);
            await WaitForTcpAsync("127.0.0.1", RedisPort);

            var natsId = await StartContainerAsync(
                NatsImage,
                ["--publish", "127.0.0.1::4222", "--entrypoint", "nats-server"],
                ["--jetstream", "--store_dir=/data", "--port", "4222", "--user", _natsUser, "--pass", _natsPassword]);
            NatsPort = await GetPublishedPortAsync(natsId, 4222);
            await WaitForTcpAsync("127.0.0.1", NatsPort);
            await CreateJetStreamStreamsAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"SwiftAid local provider fixture startup failed (redacted): error={exception.GetType().Name}; " +
                $"minio-api-port={GetPortOrNull(MinioEndpoint)?.ToString() ?? "unmapped"}; " +
                $"minio-api-readiness={_minioReadinessStatus}; " +
                $"minio-console-port={(MinioConsolePort == 0 ? "unmapped" : MinioConsolePort.ToString())}.");
            await DisposeAsync();
            throw new InvalidOperationException(
                $"The isolated local provider fixture did not start successfully ({exception.GetType().Name}); " +
                "synthetic credentials and detailed process output were suppressed.");
        }
    }

    public ServiceProvider CreateServiceProvider(string? eventStream = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["TransportSecurity:AllowPlaintextDevelopment"] = "true",
            ["TransportSecurity:AllowedDevelopmentHosts:0"] = "127.0.0.1",
            ["Minio:Enabled"] = "true",
            ["Minio:Endpoint"] = MinioEndpoint,
            ["Minio:Bucket"] = Bucket,
            ["Minio:MaxObjectSizeBytes"] = MaximumObjectBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Minio:AccessKey"] = _minioUser,
            ["Minio:SecretKey"] = _minioPassword,
            ["Minio:UseSsl"] = "false",
            ["Redis:Enabled"] = "true",
            ["Redis:ConnectionString"] = $"127.0.0.1:{RedisPort},password={_redisPassword},abortConnect=false",
            ["Redis:InstanceName"] = "swiftaid:test:",
            ["Redis:MaximumTtlSeconds"] = "3600",
            ["Nats:Enabled"] = "true",
            ["Nats:Url"] = $"nats://127.0.0.1:{NatsPort}",
            ["Nats:Username"] = _natsUser,
            ["Nats:Password"] = _natsPassword,
            ["Nats:EventStream"] = eventStream ?? EventStream,
            ["Nats:EventSubjectPrefix"] = "swiftaid.events.case",
            ["Nats:NotificationStream"] = NotificationStream,
            ["Nats:NotificationSubject"] = "swiftaid.notifications.inapp"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSwiftAidInfrastructure(configuration, new TestHostEnvironment());
        return services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        foreach (var container in _containers.AsEnumerable().Reverse())
        {
            await RemoveContainerByNameAsync(container);
        }
    }

    private async Task CreatePrivateBucketAsync()
    {
        using var client = new MinioClient()
            .WithEndpoint("127.0.0.1", GetPort(MinioEndpoint))
            .WithCredentials(_minioUser, _minioPassword)
            .WithSSL(false)
            .Build();

        await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket), CancellationToken.None);
    }

    private async Task CreateJetStreamStreamsAsync()
    {
        await using var client = new NatsClient(new NatsOpts
        {
            Url = $"nats://127.0.0.1:{NatsPort}",
            AuthOpts = new NatsAuthOpts { Username = _natsUser, Password = _natsPassword },
            ConnectTimeout = TimeSpan.FromSeconds(5),
            RequestTimeout = TimeSpan.FromSeconds(5)
        });

        var jetStream = client.CreateJetStreamContext();
        await jetStream.CreateStreamAsync(new StreamConfig
        {
            Name = EventStream,
            Subjects = ["swiftaid.events.case.>"]
        });
        await jetStream.CreateStreamAsync(new StreamConfig
        {
            Name = NotificationStream,
            Subjects = ["swiftaid.notifications.inapp"]
        });
    }

    private async Task<string> StartContainerAsync(
        string image,
        IReadOnlyList<string> dockerOptions,
        IReadOnlyList<string> command)
    {
        var fixtureId = Guid.NewGuid();
        var containerName = $"swiftaid-provider-test-{fixtureId:N}";
        var identity = new ContainerIdentity(
            fixtureId,
            containerName,
            FixtureLabel,
            $"{FixtureIdLabel}={fixtureId:N}");
        _containers.Add(identity);

        var dockerArguments = new List<string> { "run", "--pull=never", "--detach", "--rm" };
        dockerArguments.AddRange(["--name", identity.Name, "--label", identity.FixtureLabel,
            "--label", identity.FixtureIdLabel]);
        dockerArguments.AddRange(dockerOptions);
        dockerArguments.Add(image);
        dockerArguments.AddRange(command);
        string output;
        try
        {
            output = await RunDockerAsync(dockerArguments, TimeSpan.FromSeconds(30));
        }
        catch
        {
            await RemoveContainerByNameAsync(identity);
            throw;
        }

        var containerId = output.Trim();
        if (containerId.Length < 12 || containerId.Any(character => !Uri.IsHexDigit(character)))
        {
            await RemoveContainerByNameAsync(identity);
            throw new InvalidOperationException("Docker did not return a valid disposable container identifier.");
        }

        return containerId;
    }

    private static async Task RemoveContainerByNameAsync(ContainerIdentity identity)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var labels = await RunDockerAsync(
                [
                    "inspect",
                    "--format",
                    "{{index .Config.Labels \"com.swiftaid.fixture\"}}|{{index .Config.Labels \"com.swiftaid.fixture-id\"}}",
                    identity.Name
                ],
                TimeSpan.FromSeconds(10),
                allowFailure: true);
            var expectedLabels = $"{GetLabelValue(identity.FixtureLabel)}|{GetLabelValue(identity.FixtureIdLabel)}";
            if (!string.Equals(labels.Trim(), expectedLabels, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"Skipped cleanup for {identity.Name}: the exact container was absent or its fixture labels did not match.");
                return;
            }

            await RunDockerAsync(["rm", "--force", identity.Name], TimeSpan.FromSeconds(10), allowFailure: true);
            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    private static string GetLabelValue(string label) => label[(label.IndexOf('=') + 1)..];

    private async Task<int> GetPublishedPortAsync(string containerId, int containerPort)
    {
        var output = await RunDockerAsync(["port", containerId, $"{containerPort}/tcp"], TimeSpan.FromSeconds(10));
        var endpoint = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (endpoint is null || !IPEndPoint.TryParse(endpoint, out var parsed))
        {
            throw new InvalidOperationException("Docker did not return a loopback published port.");
        }

        Assert.Equal(IPAddress.Loopback, parsed.Address);
        return parsed.Port;
    }

    private static async Task EnsureDockerAvailableAsync()
    {
        await RunDockerAsync(["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(10));
        foreach (var image in new[] { MinioImage, RedisImage, NatsImage })
        {
            await RunDockerAsync(["image", "inspect", image], TimeSpan.FromSeconds(10));
        }
    }

    private static async Task<string> RunDockerAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        bool allowFailure = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Docker could not be started for the opt-in local provider fixture.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("A bounded Docker fixture command timed out.");
        }

        var output = await standardOutput;
        _ = await standardError;
        if (!allowFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"A local Docker fixture command failed with exit code {process.ExitCode}; details were suppressed to avoid echoing synthetic credentials.");
        }

        return output;
    }

    private static async Task WaitForTcpAsync(string host, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, timeout.Token);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(250, timeout.Token);
            }
        }

        throw new TimeoutException("A disposable provider container did not begin listening within 30 seconds.");
    }

    private async Task WaitForMinioHttpReadyAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var readinessUrl = $"{MinioEndpoint}/minio/health/ready";

        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync(readinessUrl, timeout.Token);
                _minioReadinessStatus = $"HTTP {(int)response.StatusCode}";
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                _minioReadinessStatus = "connection-unavailable";
            }
            catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
            {
                _minioReadinessStatus = "request-timeout";
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }

        throw new TimeoutException("The disposable MinIO HTTP readiness endpoint did not return success within 30 seconds.");
    }

    private static int GetPort(string endpoint) => new Uri(endpoint).Port;

    private static int? GetPortOrNull(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Port : null;

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "SwiftAid.Integration.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed record ContainerIdentity(Guid FixtureId, string Name, string FixtureLabel, string FixtureIdLabel);
}
