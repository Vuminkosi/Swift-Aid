using CommunityToolkit.Datasync.Client.Offline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Client.LocalStore;

namespace SwiftAid.Client.Tests;

public sealed class OfflineSpikeTests
{
    [Fact]
    public async Task SyntheticRow_PersistsInFileBackedDatasyncContextAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swiftaid-offline-spike-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var factory = new SwiftAidOfflineDbContextFactory(directory, gatewayBaseAddress: null);
            Assert.False(factory.HasRemoteEndpoint);

            await using (var firstContext = await factory.CreateDbContextAsync())
            {
                await firstContext.Database.EnsureCreatedAsync();
                firstContext.SpikeRows.Add(new OfflineSpikeRecord
                {
                    Id = "restart-proof",
                    Value = "Synthetic local row",
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Version = [1],
                    Deleted = false
                });
                await firstContext.SaveChangesAsync();
            }

            await using var reopenedContext = await factory.CreateDbContextAsync();
            var saved = await reopenedContext.SpikeRows.AsNoTracking().SingleAsync();

            Assert.Equal("restart-proof", saved.Id);
            Assert.Equal("Synthetic local row", saved.Value);
            Assert.Equal(new byte[] { 1 }, saved.Version);
            Assert.False(saved.Deleted);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RemoteEndpoint_RequiresHttpsAndDoesNotAcceptCredentialOrQueryData()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swiftaid-offline-spike-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            Assert.Throws<ArgumentException>(() => new SwiftAidOfflineDbContextFactory(directory, new Uri("http://gateway.test")));
            Assert.Throws<ArgumentException>(() => new SwiftAidOfflineDbContextFactory(directory, new Uri("https://user:secret@gateway.test")));
            Assert.Throws<ArgumentException>(() => new SwiftAidOfflineDbContextFactory(directory, new Uri("https://gateway.test/?token=secret")));

            var factory = new SwiftAidOfflineDbContextFactory(directory, new Uri("https://gateway.test/"));
            Assert.True(factory.HasRemoteEndpoint);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrBlankGatewayConfiguration_LeavesRemotePullUnavailable(string? configuredGatewayAddress)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swiftaid-offline-spike-no-gateway-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            Uri? gatewayBaseAddress = string.IsNullOrWhiteSpace(configuredGatewayAddress)
                ? null
                : new Uri(configuredGatewayAddress, UriKind.Absolute);
            var factory = new SwiftAidOfflineDbContextFactory(directory, gatewayBaseAddress);

            Assert.False(factory.HasRemoteEndpoint);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PullAsync_MergesRemoteSyntheticRowIntoFileBackedStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swiftaid-offline-spike-pull-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var endpoint = new Uri("https://gateway.test/mobile/api/tables/spike");
            using var handler = new StubHttpMessageHandler("/mobile/api/tables/spike", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"count\":1,\"items\":[{\"id\":\"P02-pull-row\",\"value\":\"Synthetic remote row\",\"updatedAt\":\"2026-10-27T12:00:00Z\",\"version\":\"AQ==\",\"deleted\":false}],\"nextLink\":null}", System.Text.Encoding.UTF8, "application/json")
            });
            using var client = new HttpClient(handler);

            await using var context = CreatePullTestContext(directory, endpoint, client);
            await context.Database.EnsureCreatedAsync();
            var result = await context.PullAsync(configure =>
                configure.AddPullRequest<OfflineSpikeRecord>());

            Assert.True(result.IsSuccessful, DescribePullFailures(result));
            var saved = await context.SpikeRows.AsNoTracking().SingleAsync(row => row.Id == "P02-pull-row");
            Assert.Equal("Synthetic remote row", saved.Value);
            Assert.Equal(new byte[] { 1 }, saved.Version);
            Assert.False(saved.Deleted);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PullAsync_UnauthenticatedResponseIsFailureAndDoesNotMergeRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swiftaid-offline-spike-unauth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var endpoint = new Uri("https://gateway.test/tables/spike");
            using var handler = new StubHttpMessageHandler("/tables/spike", _ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
            using var client = new HttpClient(handler);

            await using var context = CreatePullTestContext(directory, endpoint, client);
            await context.Database.EnsureCreatedAsync();
            var result = await context.PullAsync(configure =>
                configure.AddPullRequest<OfflineSpikeRecord>());

            Assert.False(result.IsSuccessful);
            Assert.NotEmpty(result.FailedRequests);
            Assert.Empty(await context.SpikeRows.AsNoTracking().ToListAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static PullTestDbContext CreatePullTestContext(string directory, Uri endpoint, HttpClient client)
    {
        var databasePath = Path.Combine(directory, "offline-spike-pull-test.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        var options = new DbContextOptionsBuilder<PullTestDbContext>()
            .UseSqlite(connectionString)
            .Options;

        return new PullTestDbContext(options, endpoint, client);
    }

    private static string DescribePullFailures(PullResult result)
    {
        var failedRequests = string.Join(
            "; ",
            result.FailedRequests.Select(request =>
                $"{request.Key.AbsolutePath}: {(int)request.Value.StatusCode} {request.Value.ReasonPhrase}"));
        var localExceptions = string.Join(
            "; ",
            result.LocalExceptions.Select(exception => exception.Value.GetType().Name));

        return $"Failed requests: {failedRequests}. Local exceptions: {localExceptions}.";
    }

    private sealed class PullTestDbContext(
        DbContextOptions<PullTestDbContext> options,
        Uri endpoint,
        HttpClient client) : OfflineDbContext(options)
    {
        public DbSet<OfflineSpikeRecord> SpikeRows => Set<OfflineSpikeRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            var row = modelBuilder.Entity<OfflineSpikeRecord>();
            row.ToTable("offline_spike_rows");
            row.HasKey(value => value.Id);
            row.Property(value => value.Id).HasMaxLength(64).IsRequired();
            row.Property(value => value.Value).HasMaxLength(200).IsRequired();
            row.Property(value => value.UpdatedAt);
            row.Property(value => value.Version);
            row.Property(value => value.Deleted).IsRequired();
        }

        protected override void OnDatasyncInitialization(DatasyncOfflineOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseHttpClient(client);
            optionsBuilder.Entity<OfflineSpikeRecord>(entity => entity.Endpoint = endpoint);
        }
    }

    private sealed class StubHttpMessageHandler(string expectedPath, Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https", request.RequestUri?.Scheme);
            Assert.Equal(expectedPath, request.RequestUri?.AbsolutePath);
            return Task.FromResult(responseFactory(request));
        }
    }
}
