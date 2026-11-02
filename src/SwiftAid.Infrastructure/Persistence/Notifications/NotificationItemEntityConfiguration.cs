using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftAid.Domain.Notifications;

namespace SwiftAid.Infrastructure.Persistence.Notifications;

/// <summary>PostgreSQL mapping for recipient-scoped notification occurrences.</summary>
public sealed class NotificationItemEntityConfiguration : IEntityTypeConfiguration<NotificationItem>
{
    public void Configure(EntityTypeBuilder<NotificationItem> builder)
    {
        builder.ToTable("notification_items", "swiftaid_notifications", table =>
        {
            table.HasCheckConstraint(
                "ck_notification_items_kind",
                "kind IN ('CaseNeedsAttention')");
            table.HasCheckConstraint(
                "ck_notification_items_resource_kind",
                "resource_kind IN ('ManagedCase')");
            table.HasCheckConstraint(
                "ck_notification_items_action",
                "action IN ('OpenCase')");
            table.HasCheckConstraint(
                "ck_notification_items_recipient_requirement",
                "recipient_requirement IN ('CurrentAccountableOwner', 'AuthorizedCaseMember', 'CurrentFollowUpAssignee')");
            table.HasCheckConstraint(
                "ck_notification_items_related_resource_pair",
                "(related_resource_id IS NULL) = (related_resource_version IS NULL)");
            table.HasCheckConstraint(
                "ck_notification_items_follow_up_reference",
                "(recipient_requirement = 'CurrentFollowUpAssignee') = (related_resource_id IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_notification_items_read_after_created",
                "read_at_utc IS NULL OR read_at_utc >= created_at_utc");
            table.HasCheckConstraint(
                "ck_notification_items_dismissed_after_created",
                "dismissed_at_utc IS NULL OR dismissed_at_utc >= created_at_utc");
            table.HasCheckConstraint(
                "ck_notification_items_actor_nonempty",
                "length(btrim(recipient_actor_id)) > 0");
            table.HasCheckConstraint(
                "ck_notification_items_occurrence_key_nonempty",
                "length(btrim(occurrence_key)) > 0");
            table.HasCheckConstraint(
                "ck_notification_items_accepted_event_nonempty",
                "accepted_event_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        });

        builder.HasKey(item => item.NotificationId)
            .HasName("pk_notification_items");
        builder.Property(item => item.NotificationId)
            .HasColumnName("notification_id")
            .HasColumnType("uuid");
        builder.Property(item => item.OccurrenceKey)
            .HasColumnName("occurrence_key")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(item => item.AcceptedEventId)
            .HasColumnName("accepted_event_id")
            .HasColumnType("uuid")
            .IsRequired();
        builder.Property(item => item.RecipientActorId)
            .HasColumnName("recipient_actor_id")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(item => item.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasColumnType("text")
            .IsRequired();
        builder.Property(item => item.ResourceKind)
            .HasColumnName("resource_kind")
            .HasConversion<string>()
            .HasColumnType("text")
            .IsRequired();
        builder.Property(item => item.ResourceId)
            .HasColumnName("resource_id")
            .HasColumnType("uuid")
            .IsRequired();
        builder.Property(item => item.RelatedResourceId)
            .HasColumnName("related_resource_id")
            .HasColumnType("uuid");
        builder.Property(item => item.RelatedResourceVersion)
            .HasColumnName("related_resource_version")
            .HasColumnType("bigint");
        builder.Property(item => item.Action)
            .HasColumnName("action")
            .HasConversion<string>()
            .HasColumnType("text")
            .IsRequired();
        builder.Property(item => item.RecipientRequirement)
            .HasColumnName("recipient_requirement")
            .HasConversion<string>()
            .HasColumnType("text")
            .IsRequired();
        builder.Property(item => item.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
        builder.Property(item => item.ReadAtUtc)
            .HasColumnName("read_at_utc")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.DismissedAtUtc)
            .HasColumnName("dismissed_at_utc")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(item => new { item.RecipientActorId, item.OccurrenceKey })
            .IsUnique()
            .HasDatabaseName("ux_notification_items_recipient_occurrence");
        builder.HasIndex(item => new { item.RecipientActorId, item.CreatedAtUtc, item.NotificationId })
            .HasDatabaseName("ix_notification_items_recipient_created");
        builder.HasOne<ActorEntity>()
            .WithMany()
            .HasForeignKey(item => item.RecipientActorId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_notification_items_recipient_actor");
        builder.HasOne<CaseEventEntity>()
            .WithMany()
            .HasForeignKey(item => item.AcceptedEventId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_notification_items_accepted_event");
        builder.HasOne<ManagedCaseEntity>()
            .WithMany()
            .HasForeignKey(item => item.ResourceId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_notification_items_managed_case");
    }
}
