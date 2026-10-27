using Microsoft.EntityFrameworkCore;

namespace SwiftAid.Client.LocalStore;

/// <summary>Separate app-private SQLite database; this context is never a Datasync projection.</summary>
public sealed class LocalDraftDbContext(DbContextOptions<LocalDraftDbContext> options) : DbContext(options)
{
    public DbSet<EncryptedDraftRow> Drafts => Set<EncryptedDraftRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<EncryptedDraftRow>();
        row.ToTable("local_report_drafts");
        row.HasKey(value => value.DraftId);
        row.Property(value => value.DraftId).HasMaxLength(36).IsRequired();
        row.Property(value => value.OwnerActorId).HasMaxLength(200).IsRequired();
        row.Property(value => value.WorkspaceId).HasMaxLength(200).IsRequired();
        row.Property(value => value.SchemaVersion).IsRequired();
        row.Property(value => value.AccessEpoch).IsRequired();
        row.Property(value => value.Nonce).IsRequired();
        row.Property(value => value.Ciphertext).IsRequired();
        row.Property(value => value.Tag).IsRequired();
        row.HasIndex(value => new { value.OwnerActorId, value.WorkspaceId });
    }
}

/// <summary>
/// Creates independent contexts over a file-backed database path supplied by the native host from
/// its app-private data directory. The path must not point at a Datasync projection database.
/// </summary>
public sealed class LocalDraftDbContextFactory : IDbContextFactory<LocalDraftDbContext>
{
    private readonly string _connectionString;

    public LocalDraftDbContextFactory(string appPrivateDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appPrivateDatabasePath);
        if (!Path.IsPathFullyQualified(appPrivateDatabasePath) || appPrivateDatabasePath == ":memory:")
            throw new ArgumentException("A fully qualified local database file path is required.", nameof(appPrivateDatabasePath));

        _connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = appPrivateDatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
    }

    public LocalDraftDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<LocalDraftDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new LocalDraftDbContext(options);
    }

    public Task<LocalDraftDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
}

public sealed class EncryptedDraftRow
{
    /// <summary>Version 2 binds the authorization epoch into AES-GCM associated data.</summary>
    public const int CurrentEncryptionVersion = 2;

    public string DraftId { get; set; } = string.Empty;
    public string OwnerActorId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public long AccessEpoch { get; set; }
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Tag { get; set; } = [];
}
