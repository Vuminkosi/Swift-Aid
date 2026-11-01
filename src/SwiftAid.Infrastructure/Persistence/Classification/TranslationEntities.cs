using Microsoft.EntityFrameworkCore;
using SwiftAid.Infrastructure.Persistence.Entities.Cases;

namespace SwiftAid.Infrastructure.Persistence.Classification;

public sealed class TranslationArtifactEntity
{
    public Guid Id { get; set; }
    public required string OrganizationId { get; set; }
    public required string WorkspaceId { get; set; }
    public required string Fingerprint { get; set; }
    public required string ContentHash { get; set; }
    public required string SourceLanguage { get; set; }
    public required string ModelRevision { get; set; }
    public required string Configuration { get; set; }
    public required byte[] ProtectedSource { get; set; }
    public byte[]? ProtectedTranslation { get; set; }
    public string State { get; set; } = "Pending";
    public string ReviewStatus { get; set; } = "Pending";
    public long ReviewVersion { get; set; }
    public string FindingsJson { get; set; } = "[]";
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public int Attempts { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public Guid? CompletedLeaseId { get; set; }
    public string? ResultHash { get; set; }
    public double InferenceMilliseconds { get; set; }
}

public sealed class TranslationAssociationEntity
{
    public Guid SourceEventId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid CaseId { get; set; }
    public long ReportRevision { get; set; }
}

public sealed class TranslationReviewEntity
{
    public Guid DecisionId { get; set; }
    public Guid ArtifactId { get; set; }
    public required string ActorId { get; set; }
    public bool Approved { get; set; }
    public long Version { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

/// <summary>Metadata-only replay of the original committed source event after review.</summary>
public sealed class TranslationOutboxEntity
{
    public Guid Id { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid SourceEventId { get; set; }
    public long ReviewVersion { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? FailureCode { get; set; }
}

public static class TranslationModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<TranslationArtifactEntity>(b =>
        {
            b.ToTable("artifacts", "swiftaid_translation", t =>
            {
                t.HasCheckConstraint("ck_translation_state", "\"State\" IN ('Pending','Leased','RetryScheduled','Completed','Failed','Cancelled')");
                t.HasCheckConstraint("ck_translation_review", "\"ReviewStatus\" IN ('Pending','Unreviewed','Approved','Rejected')");
                t.HasCheckConstraint("ck_translation_lease", "(\"State\" = 'Leased') = (\"LeaseId\" IS NOT NULL AND \"LeaseUntilUtc\" IS NOT NULL)");
                t.HasCheckConstraint("ck_translation_attempts", "\"Attempts\" BETWEEN 0 AND 5");
            });
            b.HasKey(x => x.Id).HasName("PK_translation_artifacts"); b.HasIndex(x => x.Fingerprint).IsUnique().HasDatabaseName("IX_translation_fingerprint");
            b.Property(x => x.FindingsJson).HasColumnType("jsonb");
            b.HasIndex(x => new { x.State, x.AvailableAtUtc }).HasDatabaseName("IX_translation_work");
            b.HasOne<WorkspaceOrganizationEntity>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.OrganizationId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_translation_workspace");
        });
        builder.Entity<TranslationAssociationEntity>(b =>
        {
            b.ToTable("associations", "swiftaid_translation");
            b.HasKey(x => new { x.SourceEventId, x.ArtifactId }).HasName("PK_translation_associations");
            b.HasIndex(x => new { x.CaseId, x.ReportRevision }).HasDatabaseName("IX_translation_source_revision");
            b.HasOne<TranslationArtifactEntity>().WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_translation_association_artifact");
            b.HasOne<CaseEventEntity>().WithMany().HasForeignKey(x => x.SourceEventId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_translation_association_event");
            b.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_translation_association_case");
        });
        builder.Entity<TranslationReviewEntity>(b =>
        {
            b.ToTable("reviews", "swiftaid_translation"); b.HasKey(x => x.DecisionId).HasName("PK_translation_reviews");
            b.HasIndex(x => new { x.ArtifactId, x.Version }).IsUnique().HasDatabaseName("IX_translation_review_version");
            b.HasOne<TranslationArtifactEntity>().WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_translation_review_artifact");
        });
        builder.Entity<TranslationOutboxEntity>(b =>
        {
            b.ToTable("outbox", "swiftaid_translation"); b.HasKey(x => x.Id).HasName("PK_translation_outbox");
            b.HasIndex(x => new { x.ArtifactId, x.SourceEventId, x.ReviewVersion }).IsUnique().HasDatabaseName("IX_translation_replay_identity");
            b.HasIndex(x => x.AvailableAtUtc).HasDatabaseName("IX_translation_outbox_available").HasFilter("\"PublishedAtUtc\" IS NULL");
            b.HasOne<TranslationArtifactEntity>().WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_translation_outbox_artifact");
            b.HasOne<CaseEventEntity>().WithMany().HasForeignKey(x => x.SourceEventId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_translation_outbox_event");
        });
    }
}
