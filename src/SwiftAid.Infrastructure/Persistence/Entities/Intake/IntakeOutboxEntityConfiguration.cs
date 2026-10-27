using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

public sealed class IntakeOutboxEntityConfiguration : IEntityTypeConfiguration<IntakeOutboxEntity>
{
    public void Configure(EntityTypeBuilder<IntakeOutboxEntity> builder)
    {
        builder.ToTable("intake_outbox", "swiftaid_intake", table =>
        {
            table.HasCheckConstraint("ck_intake_outbox_revision", "revision > 0");
            table.HasCheckConstraint("ck_intake_outbox_attempts", "attempts >= 0");
            table.HasCheckConstraint("ck_intake_outbox_event_type", "event_type IN ('IntakeReceived')");
        });
        builder.HasKey(item => item.OutboxId).HasName("pk_intake_outbox");
        builder.Property(item => item.OutboxId).HasColumnName("outbox_id").HasColumnType("uuid");
        builder.Property(item => item.ReceiptId).HasColumnName("receipt_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.Revision).HasColumnName("revision").HasColumnType("bigint").IsRequired();
        builder.Property(item => item.EventType).HasColumnName("event_type").HasColumnType("text").IsRequired();
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(item => item.PublishedAtUtc).HasColumnName("published_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(item => item.Attempts).HasColumnName("attempts").HasColumnType("integer").IsRequired();
        builder.HasIndex(item => new { item.ReceiptId, item.Revision, item.EventType }).IsUnique()
            .HasDatabaseName("ux_intake_outbox_occurrence");
        builder.HasIndex(item => new { item.CreatedAtUtc, item.OutboxId }).HasFilter("published_at_utc IS NULL")
            .HasDatabaseName("ix_intake_outbox_pending");
        builder.HasOne<IntakeReportEntity>().WithMany().HasForeignKey(item => item.ReceiptId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_intake_outbox_report");
    }
}
