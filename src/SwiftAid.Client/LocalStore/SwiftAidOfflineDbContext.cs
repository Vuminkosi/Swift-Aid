using CommunityToolkit.Datasync.Client.Offline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SwiftAid.Client.LocalStore;

/// <summary>
/// Temporary, read-only Datasync compatibility spike. This database contains only synthetic
/// demonstration rows and is separate from encrypted report drafts and production projections.
/// </summary>
public sealed class SwiftAidOfflineDbContext : OfflineDbContext
{
    private readonly Uri? _gatewayBaseAddress;

    public SwiftAidOfflineDbContext(
        DbContextOptions<SwiftAidOfflineDbContext> options,
        Uri? gatewayBaseAddress,
        bool allowInsecureTransport = false)
        : base(options)
    {
        _gatewayBaseAddress = ValidateGatewayBaseAddress(gatewayBaseAddress, allowInsecureTransport);
    }

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
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        if (_gatewayBaseAddress is null)
        {
            return;
        }

        optionsBuilder.UseEndpoint(_gatewayBaseAddress);
        optionsBuilder.Entity<OfflineSpikeRecord>(entity =>
        {
            entity.Endpoint = new Uri(_gatewayBaseAddress, "tables/spike");
        });
    }

    internal static Uri? ValidateGatewayBaseAddress(Uri? endpoint, bool allowInsecureTransport = false)
    {
        if (endpoint is null)
        {
            return null;
        }

        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
                !(allowInsecureTransport && endpoint.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("The Datasync Gateway base address must be an absolute HTTPS origin or path without credentials, query, or fragment. HTTP requires an explicit development-only override.", nameof(endpoint));
        }

        // Datasync entity endpoints are relative to the configured gateway base. A base
        // address without a trailing slash would otherwise cause Uri to replace its last
        // path segment (for example, /gateway -> /tables/spike).
        var normalizedAddress = endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);

        return normalizedAddress;
    }
}

/// <summary>Non-sensitive synthetic projection used only to test the Datasync native client.</summary>
public sealed class OfflineSpikeRecord
{
    public string Id { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTimeOffset? UpdatedAt { get; set; }

    public byte[]? Version { get; set; }

    public bool Deleted { get; set; }
}

/// <summary>Creates Datasync contexts over the app-private SQLite file used by the spike.</summary>
public sealed class SwiftAidOfflineDbContextFactory : IDbContextFactory<SwiftAidOfflineDbContext>
{
    private readonly string _databasePath;
    private readonly Uri? _gatewayBaseAddress;
    private readonly bool _allowInsecureTransport;

    public SwiftAidOfflineDbContextFactory(
        string appPrivateDirectory,
        Uri? gatewayBaseAddress,
        bool allowInsecureTransport = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appPrivateDirectory);
        if (!Path.IsPathFullyQualified(appPrivateDirectory))
        {
            throw new ArgumentException("An absolute app-private directory is required.", nameof(appPrivateDirectory));
        }

        _databasePath = Path.Combine(appPrivateDirectory, "swiftaid-datasync-spike.db");
        _allowInsecureTransport = allowInsecureTransport;
        _gatewayBaseAddress = SwiftAidOfflineDbContext.ValidateGatewayBaseAddress(gatewayBaseAddress, allowInsecureTransport);
    }

    public bool HasRemoteEndpoint => _gatewayBaseAddress is not null;

    public SwiftAidOfflineDbContext CreateDbContext()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        var options = new DbContextOptionsBuilder<SwiftAidOfflineDbContext>()
            .UseSqlite(connectionString)
            .Options;

        return new SwiftAidOfflineDbContext(options, _gatewayBaseAddress, _allowInsecureTransport);
    }

    public Task<SwiftAidOfflineDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }

}
