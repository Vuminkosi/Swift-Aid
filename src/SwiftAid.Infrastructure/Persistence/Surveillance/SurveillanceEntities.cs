using Microsoft.EntityFrameworkCore;

namespace SwiftAid.Infrastructure.Persistence.Surveillance;

public sealed class SurveillanceWorkEntity
{
    public required string Id { get; set; }
    public Guid CaseId { get; set; }
    public required string State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class SurveillanceInputEntity
{
    public Guid CaseId { get; set; }
    public required string WorkspaceId { get; set; }
    public required string OrganizationId { get; set; }
    public required string ObservationJson { get; set; }
    public required string WorkId { get; set; }
}

public sealed class DetectionAssessmentEntity
{
    public Guid Id { get; set; }
    public Guid SignalId { get; set; }
    public long Version { get; set; }
    public required string WorkId { get; set; }
    public required string EvidenceHash { get; set; }
    public required string AssessmentJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class SurveillanceSignalEntity
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    public required string WorkspaceId { get; set; }
    public required string OrganizationId { get; set; }
    public required string Area { get; set; }
    public required string Syndrome { get; set; }
    public DateTimeOffset WindowEndUtc { get; set; }
    public long Version { get; set; }
    public Guid AssessmentId { get; set; }
    public bool Candidate { get; set; }
    public required string ReviewState { get; set; }
}

public sealed class DetectionEvidenceEntity
{
    public Guid AssessmentId { get; set; }
    public Guid CaseId { get; set; }
    public Guid SourceEventId { get; set; }
    public long ReportRevision { get; set; }
}

public sealed class SurveillanceReviewEntity
{
    public Guid CommandId { get; set; }
    public Guid SignalId { get; set; }
    public Guid AssessmentId { get; set; }
    public long Version { get; set; }
    public required string ActorId { get; set; }
    public required string State { get; set; }
    public required string Rationale { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SurveillanceEventEntity
{
    public Guid Id { get; set; }
    public Guid SignalId { get; set; }
    public long Version { get; set; }
    public required string EventType { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public string? FailureCode { get; set; }
}

public static class SurveillanceModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<SurveillanceWorkEntity>(b =>
        {
            b.ToTable("work", "swiftaid_surveillance", t =>
            {
                t.HasCheckConstraint("ck_surveillance_work_state", "\"State\" IN ('Pending','RetryScheduled','Completed','Failed')");
                t.HasCheckConstraint("ck_surveillance_work_attempts", "\"Attempts\" BETWEEN 0 AND 5");
            });
            b.HasKey(x => x.Id); b.Property(x => x.Id).HasMaxLength(64);
            b.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SurveillanceInputEntity>(b =>
        {
            b.ToTable("inputs", "swiftaid_surveillance"); b.HasKey(x => x.CaseId);
            b.Property(x => x.ObservationJson).HasColumnType("jsonb");
            b.HasIndex(x => new { x.WorkspaceId, x.OrganizationId });
            b.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SurveillanceWorkEntity>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SurveillanceSignalEntity>(b =>
        {
            b.ToTable("signals", "swiftaid_surveillance"); b.HasKey(x => x.Id);
            b.HasIndex(x => x.Key).IsUnique(); b.Property(x => x.Key).HasMaxLength(64);
            b.HasIndex(x => new { x.WorkspaceId, x.OrganizationId });
            b.HasOne<WorkspaceOrganizationEntity>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<DetectionAssessmentEntity>(b =>
        {
            b.ToTable("assessments", "swiftaid_surveillance"); b.HasKey(x => x.Id);
            b.Property(x => x.AssessmentJson).HasColumnType("jsonb");
            b.HasIndex(x => new { x.SignalId, x.Version }).IsUnique();
            b.HasOne<SurveillanceSignalEntity>().WithMany().HasForeignKey(x => x.SignalId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SurveillanceWorkEntity>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SurveillanceReviewEntity>(b =>
        {
            b.ToTable("reviews", "swiftaid_surveillance"); b.HasKey(x => x.CommandId);
            b.HasIndex(x => new { x.SignalId, x.Version }).IsUnique();
            b.Property(x => x.Rationale).HasMaxLength(1000);
            b.HasOne<DetectionAssessmentEntity>().WithMany().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SurveillanceSignalEntity>().WithMany().HasForeignKey(x => x.SignalId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ActorEntity>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<DetectionEvidenceEntity>(b =>
        {
            b.ToTable("evidence", "swiftaid_surveillance"); b.HasKey(x => new { x.AssessmentId, x.CaseId });
            b.HasIndex(x => x.CaseId);
            b.HasOne<DetectionAssessmentEntity>().WithMany().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CaseEventEntity>().WithMany().HasForeignKey(x => x.SourceEventId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SurveillanceEventEntity>(b =>
        {
            b.ToTable("outbox", "swiftaid_surveillance"); b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.SignalId, x.Version }).IsUnique();
            b.HasIndex(x => x.AvailableAtUtc).HasFilter("\"PublishedAtUtc\" IS NULL AND \"Attempts\" < 5");
            b.HasOne<SurveillanceSignalEntity>().WithMany().HasForeignKey(x => x.SignalId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
