using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

public sealed class IntakeReportEntityConfiguration : IEntityTypeConfiguration<IntakeReportEntity>
{
    public void Configure(EntityTypeBuilder<IntakeReportEntity> builder)
    {
        builder.ToTable("intake_reports", "swiftaid_intake", table =>
        {
            table.HasCheckConstraint("ck_intake_reports_digest", "octet_length(canonical_payload_digest) = 32");
            table.HasCheckConstraint("ck_intake_reports_payload", "octet_length(protected_payload) > 0");
            table.HasCheckConstraint("ck_intake_reports_version", "version > 0");
            table.HasCheckConstraint("ck_intake_reports_status", "status IN ('ReceivedUnverified', 'AwaitingReview', 'AcceptedIntoCase', 'LinkedAsEvidence', 'Declined')");
            table.HasCheckConstraint("ck_intake_reports_routing", "routing_status IN ('NeedsRouting', 'RoutedForReview', 'NoServiceCoverage')");
        });
        builder.HasKey(item => item.ReceiptId).HasName("pk_intake_reports");
        builder.Property(item => item.ReceiptId).HasColumnName("receipt_id").HasColumnType("uuid");
        builder.Property(item => item.SessionId).HasColumnName("session_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.ClientSubmissionId).HasColumnName("client_submission_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.CanonicalPayloadDigest).HasColumnName("canonical_payload_digest").HasColumnType("bytea").IsRequired();
        builder.Property(item => item.ProtectedPayload).HasColumnName("protected_payload").HasColumnType("bytea").IsRequired();
        builder.Property(item => item.Status).HasColumnName("status").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(item => item.RoutingStatus).HasColumnName("routing_status").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(item => item.IntakeQueueId).HasColumnName("intake_queue_id").HasColumnType("text");
        builder.Property(item => item.Version).HasColumnName("version").HasColumnType("bigint").IsRequired();
        builder.Property(item => item.ReceivedAtUtc).HasColumnName("received_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(item => item.DecidedAtUtc).HasColumnName("decided_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(item => item.DecisionActorId).HasColumnName("decision_actor_id").HasColumnType("text");
        builder.Property(item => item.ReviewReason).HasColumnName("review_reason").HasColumnType("text");
        builder.HasIndex(item => new { item.SessionId, item.ClientSubmissionId }).IsUnique()
            .HasDatabaseName("ux_intake_reports_session_submission");
        builder.HasIndex(item => new { item.IntakeQueueId, item.Status, item.ReceivedAtUtc })
            .HasDatabaseName("ix_intake_reports_review_queue");
        builder.HasOne<IntakeSessionEntity>().WithMany().HasForeignKey(item => item.SessionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_intake_reports_session");
    }
}
