using Microsoft.EntityFrameworkCore;

namespace SwiftAid.Infrastructure.Persistence.Classification;

public sealed class ClassificationBackfillEntity
{
    public Guid Id { get; set; }
    public required string WorkerId { get; set; }
    public required string WorkspaceId { get; set; }
    public required string OrganizationId { get; set; }
    public required string ProfileJson { get; set; }
    public required string ConfigurationHash { get; set; }
    public required string State { get; set; }
    public long Eligible { get; set; }
    public long Pending { get; set; }
    public long NewlyClassified { get; set; }
    public long Reused { get; set; }
    public long Abstained { get; set; }
    public long Failed { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ClassificationBackfillItemEntity
{
    public Guid JobId { get; set; }
    public Guid SourceEventId { get; set; }
    public long Ordinal { get; set; }
    public required string State { get; set; }
    public Guid? ResultId { get; set; }
}

public static class ClassificationBackfillModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<ClassificationBackfillEntity>(b =>
        {
            b.ToTable("backfills", "swiftaid_classification", t =>
            {
                t.HasCheckConstraint("ck_backfill_state", "\"State\" IN ('Running','Paused','Completed')");
                t.HasCheckConstraint("ck_backfill_counts", "\"Eligible\" = \"Pending\" + \"NewlyClassified\" + \"Reused\" + \"Abstained\" + \"Failed\" AND LEAST(\"Pending\", \"NewlyClassified\", \"Reused\", \"Abstained\", \"Failed\") >= 0");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.ProfileJson).HasColumnType("jsonb");
            b.Property(x => x.ConfigurationHash).HasMaxLength(64);
            b.HasOne<WorkspaceOrganizationEntity>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ClassificationBackfillItemEntity>(b =>
        {
            b.ToTable("backfill_items", "swiftaid_classification", t =>
                t.HasCheckConstraint("ck_backfill_item_state", "\"State\" IN ('Pending','NewlyClassified','Reused','Abstained','Failed')"));
            b.HasKey(x => new { x.JobId, x.SourceEventId });
            b.HasIndex(x => new { x.JobId, x.Ordinal }).IsUnique();
            b.HasIndex(x => new { x.JobId, x.Ordinal }, "ix_backfill_pending")
                .HasFilter("\"State\" = 'Pending'");
            b.HasOne<ClassificationBackfillEntity>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CaseEventEntity>().WithMany().HasForeignKey(x => x.SourceEventId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ClassificationResultEntity>().WithMany().HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
