using Microsoft.EntityFrameworkCore;

namespace SwiftAid.Infrastructure.Persistence.Classification;

public sealed class ReportQualityEntity
{
    public Guid SourceEventId { get; set; }
    public required string Version { get; set; }
    public Guid CaseId { get; set; }
    public long ReportRevision { get; set; }
    public required string AssessmentJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public static class ReportQualityModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<ReportQualityEntity>(b =>
        {
            b.ToTable("quality_assessments", "swiftaid_classification");
            b.HasKey(x => new { x.SourceEventId, x.Version });
            b.Property(x => x.Version).HasMaxLength(100);
            b.Property(x => x.AssessmentJson).HasColumnType("jsonb");
            b.HasIndex(x => new { x.CaseId, x.ReportRevision });
            b.HasOne<CaseEventEntity>().WithMany().HasForeignKey(x => x.SourceEventId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
