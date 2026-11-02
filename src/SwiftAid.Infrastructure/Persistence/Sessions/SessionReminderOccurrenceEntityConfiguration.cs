using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

public sealed class SessionReminderOccurrenceEntityConfiguration : IEntityTypeConfiguration<SessionReminderOccurrenceEntity>
{
    public void Configure(EntityTypeBuilder<SessionReminderOccurrenceEntity> builder)
    {
        builder.ToTable("session_reminder_occurrences", table =>
        {
            table.HasCheckConstraint("ck_session_reminder_occurrences_status", "status IN ('Pending', 'Suppressed', 'Sent')");
            table.HasCheckConstraint("ck_session_reminder_occurrences_lead", "lead_hours BETWEEN 1 AND 168");
            table.HasCheckConstraint("ck_session_reminder_occurrences_version", "version > 0");
        });

        builder.HasKey(entity => entity.OccurrenceId).HasName("pk_session_reminder_occurrences");
        builder.HasIndex(entity => new { entity.SessionId, entity.Version, entity.LeadHours })
            .IsUnique()
            .HasDatabaseName("ux_session_reminder_occurrences_session_version_lead");
        builder.HasIndex(entity => new { entity.SessionId, entity.Status, entity.RemindAtUtc })
            .HasDatabaseName("ix_session_reminder_occurrences_due");
        builder.HasIndex(entity => new { entity.Status, entity.RemindAtUtc, entity.OccurrenceId })
            .HasDatabaseName("ix_session_reminder_occurrences_global_due");
        builder.Property(entity => entity.OccurrenceId).HasColumnName("occurrence_id").HasColumnType("uuid");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id").HasColumnType("uuid");
        builder.Property(entity => entity.RemindAtUtc).HasColumnName("remind_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.LeadHours).HasColumnName("lead_hours");
        builder.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text");
        builder.Property(entity => entity.Version).HasColumnName("version");
        builder.HasOne<CommunitySessionEntity>().WithMany()
            .HasForeignKey(entity => entity.SessionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_session_reminder_occurrences_session");
    }
}
