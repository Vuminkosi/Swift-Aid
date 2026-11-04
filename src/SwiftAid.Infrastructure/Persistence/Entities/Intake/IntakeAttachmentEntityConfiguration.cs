using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

public sealed class IntakeAttachmentEntityConfiguration : IEntityTypeConfiguration<IntakeAttachmentEntity>
{
    public void Configure(EntityTypeBuilder<IntakeAttachmentEntity> builder)
    {
        builder.ToTable("intake_attachments", "swiftaid_intake", table =>
        {
            table.HasCheckConstraint("ck_intake_attachments_size", "size_bytes BETWEEN 1 AND 104857600");
            table.HasCheckConstraint("ck_intake_attachments_duration", "duration_seconds BETWEEN 1 AND 600");
            table.HasCheckConstraint("ck_intake_attachments_validity", "expires_at_utc > created_at_utc");
            table.HasCheckConstraint("ck_intake_attachments_state", "state IN ('Uploaded', 'Attached', 'Rejected', 'Expired')");
            table.HasCheckConstraint("ck_intake_attachments_keys", "length(btrim(object_key)) BETWEEN 1 AND 512");
            table.HasCheckConstraint("ck_intake_attachments_content_type", "length(btrim(content_type)) BETWEEN 1 AND 128");
        });
        builder.HasKey(item => item.AttachmentId).HasName("pk_intake_attachments");
        builder.Property(item => item.AttachmentId).HasColumnName("attachment_id").HasColumnType("uuid");
        builder.Property(item => item.SessionId).HasColumnName("session_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.CapabilityId).HasColumnName("capability_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.ClientSubmissionId).HasColumnName("client_submission_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.ObjectKey).HasColumnName("object_key").HasColumnType("text").IsRequired();
        builder.Property(item => item.ContentType).HasColumnName("content_type").HasColumnType("text").IsRequired();
        builder.Property(item => item.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint").IsRequired();
        builder.Property(item => item.DurationSeconds).HasColumnName("duration_seconds").HasColumnType("integer").IsRequired();
        builder.Property(item => item.State).HasColumnName("state").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(item => item.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(item => item.AttachedAtUtc).HasColumnName("attached_at_utc").HasColumnType("timestamp with time zone");
        builder.HasIndex(item => new { item.CapabilityId, item.SessionId, item.ClientSubmissionId, item.AttachmentId })
            .HasDatabaseName("ix_intake_attachments_binding");
        builder.HasIndex(item => item.ObjectKey).IsUnique().HasDatabaseName("ux_intake_attachments_object_key");
        builder.HasIndex(item => new { item.ExpiresAtUtc, item.State }).HasDatabaseName("ix_intake_attachments_expiry");
        builder.HasOne<IntakeSessionEntity>().WithMany().HasForeignKey(item => item.SessionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_intake_attachments_session");
        builder.HasOne<IntakeCapabilityEntity>().WithMany().HasForeignKey(item => item.CapabilityId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_intake_attachments_capability");
    }
}
