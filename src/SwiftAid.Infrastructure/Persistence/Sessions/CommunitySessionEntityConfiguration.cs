using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

public sealed class CommunitySessionEntityConfiguration : IEntityTypeConfiguration<CommunitySessionEntity>
{
    public void Configure(EntityTypeBuilder<CommunitySessionEntity> builder)
    {
        builder.ToTable("community_sessions", table =>
        {
            table.HasCheckConstraint("ck_community_sessions_version", "version > 0");
            table.HasCheckConstraint("ck_community_sessions_status", "status IN ('Draft', 'Reviewed', 'Scheduled', 'Completed', 'Cancelled')");
            table.HasCheckConstraint("ck_community_sessions_visibility", "visibility IN ('Public', 'Internal')");
            table.HasCheckConstraint("ck_community_sessions_channel", "channel_type IN ('None', 'FacebookPage', 'WhatsAppGroup', 'Other')");
            table.HasCheckConstraint("ck_community_sessions_duration", "duration_minutes BETWEEN 1 AND 1440");
            table.HasCheckConstraint(
                "ck_community_sessions_ids_nonempty",
                "length(btrim(title)) > 0 AND length(btrim(country_code)) > 0 AND length(btrim(region_id)) > 0 " +
                "AND length(btrim(created_by)) > 0 AND length(btrim(time_zone_id)) > 0");
        });

        builder.HasKey(entity => entity.SessionId).HasName("pk_community_sessions");
        builder.HasIndex(entity => new { entity.Visibility, entity.Status, entity.StartUtc })
            .HasDatabaseName("ix_community_sessions_public_listing");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id").HasColumnType("uuid");
        builder.Property(entity => entity.OrganizationId).HasColumnName("organization_id").HasColumnType("text");
        builder.HasIndex(entity => new { entity.OrganizationId, entity.StartUtc }).HasDatabaseName("ix_community_sessions_organization_schedule");
        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(entity => entity.OrganizationId)
            .HasPrincipalKey(organization => organization.OrganizationId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_community_sessions_organization");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.Title).HasColumnName("title").HasColumnType("text");
        builder.Property(entity => entity.Summary).HasColumnName("summary").HasColumnType("text");
        builder.Property(entity => entity.AudienceDescription).HasColumnName("audience_description").HasColumnType("text");
        builder.Property(entity => entity.TopicTagsJson).HasColumnName("topic_tags").HasColumnType("jsonb");
        builder.Property(entity => entity.CountryCode).HasColumnName("country_code").HasColumnType("text");
        builder.Property(entity => entity.RegionId).HasColumnName("region_id").HasColumnType("text");
        builder.Property(entity => entity.Sector).HasColumnName("sector").HasColumnType("text");
        builder.Property(entity => entity.Presenter).HasColumnName("presenter").HasColumnType("text");
        builder.Property(entity => entity.Language).HasColumnName("language").HasColumnType("text");
        builder.Property(entity => entity.StartUtc).HasColumnName("start_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.TimeZoneId).HasColumnName("time_zone_id").HasColumnType("text");
        builder.Property(entity => entity.DurationMinutes).HasColumnName("duration_minutes");
        builder.Property(entity => entity.Capacity).HasColumnName("capacity");
        builder.Property(entity => entity.MaterialsJson).HasColumnName("materials").HasColumnType("jsonb");
        builder.Property(entity => entity.Visibility).HasColumnName("visibility").HasColumnType("text");
        builder.Property(entity => entity.ChannelType).HasColumnName("channel_type").HasColumnType("text");
        builder.Property(entity => entity.ChannelLink).HasColumnName("channel_link").HasColumnType("text");
        builder.Property(entity => entity.ReminderLeadHoursJson).HasColumnName("reminder_lead_hours").HasColumnType("jsonb");
        builder.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text");
        builder.Property(entity => entity.CreatedByActorId).HasColumnName("created_by").HasColumnType("text");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.ReviewedByActorId).HasColumnName("reviewed_by").HasColumnType("text");
        builder.Property(entity => entity.ReviewedAtUtc).HasColumnName("reviewed_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.SelfReviewAcknowledged).HasColumnName("self_review_acknowledged");
        builder.Property(entity => entity.ScheduledAtUtc).HasColumnName("scheduled_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.PublishedVersion).HasColumnName("published_version");
        builder.Property(entity => entity.CancellationReason).HasColumnName("cancellation_reason").HasColumnType("text");
        builder.Property(entity => entity.CancelledAtUtc).HasColumnName("cancelled_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.CompletedAtUtc).HasColumnName("completed_at_utc").HasColumnType("timestamp with time zone");
    }
}
