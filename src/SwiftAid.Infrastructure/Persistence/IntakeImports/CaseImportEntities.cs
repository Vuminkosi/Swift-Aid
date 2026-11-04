using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.IntakeImports;

public sealed class CaseImportEntity
{
    public Guid ImportId { get; set; }
    public required string ActorId { get; set; }
    public required string WorkspaceId { get; set; }
    public string? OrganizationId { get; set; }
    public required string CountryCode { get; set; }
    public required byte[] ContextHash { get; set; }
    public required string State { get; set; }
    public long NextRow { get; set; } = 1;
    public long? FinalRow { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class CaseImportRowEntity
{
    public Guid ImportId { get; set; }
    public long RowNumber { get; set; }
    public Guid? CommandId { get; set; }
    public required byte[] SourceHash { get; set; }
    public required byte[] ProtectedSource { get; set; }
    public required string State { get; set; }
    public int Attempts { get; set; }
    public Guid? CaseId { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class CaseImportConfiguration : IEntityTypeConfiguration<CaseImportEntity>
{
    public void Configure(EntityTypeBuilder<CaseImportEntity> builder)
    {
        builder.ToTable("case_imports", "swiftaid_ingestion", table =>
        {
            table.HasCheckConstraint("ck_case_import_cursor", "\"NextRow\" > 0 AND (\"FinalRow\" IS NULL OR \"FinalRow\" >= 0)");
            table.HasCheckConstraint("ck_case_import_hash", "octet_length(\"ContextHash\") = 32");
            table.HasCheckConstraint("ck_case_import_lease", "(\"LeaseId\" IS NULL) = (\"LeaseUntilUtc\" IS NULL)");
            table.HasCheckConstraint("ck_case_import_state", "\"State\" IN ('Running','Paused','Completed','Failed')");
        });
        builder.HasKey(row => row.ImportId);
        builder.Property(row => row.ActorId).HasMaxLength(200);
        builder.Property(row => row.WorkspaceId).HasMaxLength(200);
        builder.Property(row => row.OrganizationId).HasMaxLength(200);
        builder.HasOne<WorkspaceOrganizationEntity>().WithMany().HasForeignKey(row => new { row.WorkspaceId, row.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.Property(row => row.CountryCode).HasMaxLength(2);
        builder.Property(row => row.State).HasMaxLength(32);
        builder.Property(row => row.ErrorCode).HasMaxLength(80);
        builder.HasIndex(row => new { row.ActorId, row.UpdatedAtUtc });
        builder.HasOne<ActorEntity>().WithMany().HasForeignKey(row => row.ActorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkspaceEntity>().WithMany().HasForeignKey(row => row.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CaseImportRowConfiguration : IEntityTypeConfiguration<CaseImportRowEntity>
{
    public void Configure(EntityTypeBuilder<CaseImportRowEntity> builder)
    {
        builder.ToTable("case_import_rows", "swiftaid_ingestion", table =>
        {
            table.HasCheckConstraint("ck_case_import_row_bounds", "\"RowNumber\" > 0 AND \"Attempts\" >= 0 AND octet_length(\"SourceHash\") = 32 AND octet_length(\"ProtectedSource\") BETWEEN 1 AND 524288");
            table.HasCheckConstraint("ck_case_import_row_state", "\"State\" IN ('Processing','Accepted','Replayed','Invalid','Denied','Conflict','RetryableFailure','Failed')");
        });
        builder.HasKey(row => new { row.ImportId, row.RowNumber });
        builder.Property(row => row.State).HasMaxLength(32);
        builder.Property(row => row.ErrorCode).HasMaxLength(80);
        builder.HasIndex(row => new { row.ImportId, row.State });
        builder.HasOne<CaseImportEntity>().WithMany().HasForeignKey(row => row.ImportId).OnDelete(DeleteBehavior.Restrict);
    }
}
