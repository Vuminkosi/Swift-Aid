using Microsoft.EntityFrameworkCore;

namespace SwiftAid.Infrastructure.Persistence.Classification;

public sealed class ClassificationConfigurationEntity
{
    public required string Id { get; set; }
    public required string ProfileJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class ClassificationResultEntity
{
    public Guid Id { get; set; }
    public required string Fingerprint { get; set; }
    public required string OrganizationId { get; set; }
    public required string WorkspaceId { get; set; }
    public required string ConfigurationId { get; set; }
    public required string ContentHash { get; set; }
    public required string State { get; set; }
    public Guid? LeaseId { get; set; }
    public string? WorkerId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset? AttemptStartedAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? FailureCode { get; set; }
    public string? PredictionsJson { get; set; }
    public string? AbstentionReason { get; set; }
    public int InputTokens { get; set; }
    public double InferenceMilliseconds { get; set; }
}

public sealed class ClassificationAssociationEntity
{
    public Guid CaseId { get; set; }
    public long ReportRevision { get; set; }
    public required string ConfigurationId { get; set; }
    public Guid SourceEventId { get; set; }
    public Guid ResultId { get; set; }
    public bool Reused { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class ClassificationInboxEntity
{
    public Guid MessageId { get; set; }
    public required string ConfigurationId { get; set; }
    public required string NoticeHash { get; set; }
    public required string State { get; set; }
    public Guid? ResultId { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Metadata-only transactional outbox; derived intelligence never enters the clinical event journal.</summary>
public sealed class ClassificationEventEntity
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public Guid ResultId { get; set; }
    public Guid? CaseId { get; set; }
    public long? ReportRevision { get; set; }
    public required string EventType { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public int Attempts { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? ExhaustedAtUtc { get; set; }
}

public sealed class ClassificationQuarantineEntity
{
    public required string WorkerId { get; set; }
    public ulong StreamSequence { get; set; }
    public required string PayloadSha256 { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public static class ClassificationModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<ClassificationConfigurationEntity>(b =>
        {
            b.ToTable("configurations", "swiftaid_classification"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(64); b.Property(x => x.ProfileJson).HasColumnType("jsonb");
        });
        builder.Entity<ClassificationResultEntity>(b =>
        {
            b.ToTable("results", "swiftaid_classification", t =>
            {
                t.HasCheckConstraint("ck_classification_state", "\"State\" IN ('Pending','Leased','RetryScheduled','Completed','Failed')");
                t.HasCheckConstraint("ck_classification_lease", "(\"State\" = 'Leased') = (\"LeaseId\" IS NOT NULL AND \"LeaseUntilUtc\" IS NOT NULL AND \"WorkerId\" IS NOT NULL)");
                t.HasCheckConstraint("ck_classification_attempts", "\"Attempts\" BETWEEN 0 AND 8");
            });
            b.HasKey(x => x.Id); b.HasIndex(x => x.Fingerprint).IsUnique();
            b.Property(x => x.Fingerprint).HasMaxLength(64); b.Property(x => x.ContentHash).HasMaxLength(64);
            b.Property(x => x.PredictionsJson).HasColumnType("jsonb");
            b.HasIndex(x => new { x.State, x.AvailableAtUtc });
            b.HasOne<ClassificationConfigurationEntity>().WithMany().HasForeignKey(x => x.ConfigurationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<WorkspaceOrganizationEntity>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ClassificationAssociationEntity>(b =>
        {
            b.ToTable("associations", "swiftaid_classification"); b.HasKey(x => new { x.CaseId, x.ReportRevision, x.ConfigurationId });
            b.HasIndex(x => x.ResultId); b.HasOne<ClassificationResultEntity>().WithMany().HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CaseEventEntity>().WithMany().HasForeignKey(x => x.SourceEventId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ClassificationInboxEntity>(b =>
        {
            b.ToTable("inbox", "swiftaid_classification"); b.HasKey(x => new { x.MessageId, x.ConfigurationId });
            b.Property(x => x.NoticeHash).HasMaxLength(64); b.HasIndex(x => x.ResultId);
            b.HasOne<ClassificationResultEntity>().WithMany().HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ClassificationEventEntity>(b =>
        {
            b.ToTable("outbox", "swiftaid_classification"); b.HasKey(x => x.Id);
            b.Property(x => x.Sequence).UseIdentityByDefaultColumn();
            b.HasIndex(x => x.Sequence).IsUnique();
            // The filtered supersession key cannot support ordering queries over every lifecycle event.
            b.HasIndex(x => x.ResultId);
            b.HasIndex(x => new { x.ResultId, x.CaseId, x.ReportRevision, x.EventType }).IsUnique()
                .HasFilter("\"EventType\" = 'ClassificationSuperseded'");
            b.HasIndex(x => new { x.AvailableAtUtc, x.CreatedAtUtc }).HasFilter("\"PublishedAtUtc\" IS NULL AND \"ExhaustedAtUtc\" IS NULL");
            b.HasOne<ClassificationResultEntity>().WithMany().HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ClassificationQuarantineEntity>(b =>
        {
            b.ToTable("quarantine", "swiftaid_classification"); b.HasKey(x => new { x.WorkerId, x.StreamSequence });
            b.Property(x => x.WorkerId).HasMaxLength(100); b.Property(x => x.PayloadSha256).HasMaxLength(64);
            b.Property(x => x.StreamSequence).HasConversion<decimal>();
        });
    }
}
