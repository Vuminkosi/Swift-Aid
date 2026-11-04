using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftAid.Domain.Notifications;

namespace SwiftAid.Infrastructure.Persistence.Notifications;

public sealed class NotificationDeliveryOutboxEntityConfiguration
    : IEntityTypeConfiguration<NotificationDeliveryOutboxEntity>
{
    public void Configure(EntityTypeBuilder<NotificationDeliveryOutboxEntity> builder)
    {
        builder.ToTable("notification_delivery_outbox", "swiftaid_notifications", table =>
        {
            table.HasCheckConstraint(
                "ck_notification_delivery_outbox_attempt_count",
                "attempt_count BETWEEN 0 AND 8");
            table.HasCheckConstraint(
                "ck_notification_delivery_outbox_lease_pair",
                "(lease_id IS NULL) = (lease_until_utc IS NULL)");
            table.HasCheckConstraint(
                "ck_notification_delivery_outbox_terminal_state",
                "(published_at_utc IS NULL OR lease_id IS NULL) AND (exhausted_at_utc IS NULL OR lease_id IS NULL)");
            table.HasCheckConstraint(
                "ck_notification_delivery_outbox_not_both_terminal",
                "published_at_utc IS NULL OR exhausted_at_utc IS NULL");
        });

        builder.HasKey(item => item.OutboxId)
            .HasName("pk_notification_delivery_outbox");
        builder.Property(item => item.OutboxId)
            .HasColumnName("outbox_id")
            .HasColumnType("uuid");
        builder.Property(item => item.NotificationId)
            .HasColumnName("notification_id")
            .HasColumnType("uuid")
            .IsRequired();
        builder.Property(item => item.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
        builder.Property(item => item.AvailableAtUtc)
            .HasColumnName("available_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
        builder.Property(item => item.AttemptCount)
            .HasColumnName("attempt_count")
            .IsRequired();
        builder.Property(item => item.LeaseId)
            .HasColumnName("lease_id")
            .HasColumnType("uuid");
        builder.Property(item => item.LeaseUntilUtc)
            .HasColumnName("lease_until_utc")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.PublishedAtUtc)
            .HasColumnName("published_at_utc")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.ExhaustedAtUtc)
            .HasColumnName("exhausted_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(item => item.NotificationId)
            .IsUnique()
            .HasDatabaseName("ux_notification_delivery_outbox_notification");
        builder.HasIndex(item => new { item.AvailableAtUtc, item.CreatedAtUtc, item.OutboxId })
            .HasFilter("published_at_utc IS NULL AND exhausted_at_utc IS NULL")
            .HasDatabaseName("ix_notification_delivery_outbox_pending");
        builder.HasOne<NotificationItem>()
            .WithMany()
            .HasForeignKey(item => item.NotificationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_notification_delivery_outbox_notification");
    }
}
