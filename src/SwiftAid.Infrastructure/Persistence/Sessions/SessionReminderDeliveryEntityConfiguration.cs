using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

public sealed class SessionReminderDeliveryEntityConfiguration : IEntityTypeConfiguration<SessionReminderDeliveryEntity>
{
    public void Configure(EntityTypeBuilder<SessionReminderDeliveryEntity> builder)
    {
        builder.ToTable("session_reminder_deliveries", table =>
        {
            table.HasCheckConstraint("ck_session_reminder_deliveries_channel", "channel IN ('Email', 'MobilePush')");
            table.HasCheckConstraint("ck_session_reminder_deliveries_status",
                "status IN ('Pending', 'Submitted', 'Suppressed', 'OutcomeUnknown')");
            table.HasCheckConstraint("ck_session_reminder_deliveries_versions",
                "session_version > 0 AND subscription_version > 0 AND attempt_count >= 0");
            table.HasCheckConstraint("ck_session_reminder_deliveries_lease",
                "(lease_id IS NULL) = (lease_until_utc IS NULL)");
        });

        builder.HasKey(item => item.DeliveryId).HasName("pk_session_reminder_deliveries");
        builder.HasIndex(item => new
            { item.OccurrenceId, item.AccountId, item.SubscriptionVersion, item.Channel })
            .IsUnique().HasDatabaseName("ux_session_reminder_deliveries_occurrence_recipient_version_channel");
        builder.HasIndex(item => new { item.Status, item.AvailableAtUtc, item.LeaseUntilUtc })
            .HasDatabaseName("ix_session_reminder_deliveries_claim");
        builder.HasIndex(item => new { item.SessionId, item.Status })
            .HasDatabaseName("ix_session_reminder_deliveries_session_status");

        builder.Property(item => item.DeliveryId).HasColumnName("delivery_id").HasColumnType("uuid");
        builder.Property(item => item.OccurrenceId).HasColumnName("occurrence_id").HasColumnType("uuid");
        builder.Property(item => item.SessionId).HasColumnName("session_id").HasColumnType("uuid");
        builder.Property(item => item.AccountId).HasColumnName("account_id").HasColumnType("text");
        builder.Property(item => item.Channel).HasColumnName("channel").HasColumnType("text");
        builder.Property(item => item.SessionVersion).HasColumnName("session_version");
        builder.Property(item => item.SubscriptionVersion).HasColumnName("subscription_version");
        builder.Property(item => item.ConsentReference).HasColumnName("consent_reference").HasColumnType("text");
        builder.Property(item => item.Status).HasColumnName("status").HasColumnType("text");
        builder.Property(item => item.AvailableAtUtc).HasColumnName("available_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(item => item.LeaseId).HasColumnName("lease_id").HasColumnType("uuid");
        builder.Property(item => item.LeaseUntilUtc).HasColumnName("lease_until_utc").HasColumnType("timestamp with time zone");
        builder.Property(item => item.AttemptCount).HasColumnName("attempt_count");
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(item => item.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");

        builder.HasOne<CommunitySessionEntity>().WithMany()
            .HasForeignKey(item => item.SessionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_session_reminder_deliveries_session");
    }
}
