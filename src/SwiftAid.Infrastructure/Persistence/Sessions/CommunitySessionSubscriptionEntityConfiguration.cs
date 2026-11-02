using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

public sealed class CommunitySessionSubscriptionEntityConfiguration : IEntityTypeConfiguration<CommunitySessionSubscriptionEntity>
{
    public void Configure(EntityTypeBuilder<CommunitySessionSubscriptionEntity> builder)
    {
        builder.ToTable("community_session_subscriptions", table =>
        {
            table.HasCheckConstraint("ck_community_session_subscriptions_status", "status IN ('Active', 'Unsubscribed')");
            table.HasCheckConstraint("ck_community_session_subscriptions_values", "length(btrim(account_id)) > 0 AND length(btrim(channel)) > 0 AND length(btrim(consent_reference)) > 0 AND version > 0");
            table.HasCheckConstraint("ck_community_session_subscriptions_channel", "channel IN ('Email', 'MobilePush')");
        });
        builder.HasKey(item => new { item.SessionId, item.AccountId }).HasName("pk_community_session_subscriptions");
        builder.Property(item => item.SessionId).HasColumnName("session_id").HasColumnType("uuid");
        builder.Property(item => item.AccountId).HasColumnName("account_id").HasColumnType("text").HasMaxLength(200).IsRequired();
        builder.Property(item => item.Channel).HasColumnName("channel").HasColumnType("text").IsRequired();
        builder.Property(item => item.ConsentReference).HasColumnName("consent_reference").HasColumnType("text").IsRequired();
        builder.Property(item => item.Status).HasColumnName("status").HasColumnType("text").IsRequired();
        builder.Property(item => item.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        builder.Property(item => item.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(item => new { item.AccountId, item.Status }).HasDatabaseName("ix_community_session_subscriptions_account_status");
        builder.HasOne<CommunitySessionEntity>().WithMany().HasForeignKey(item => item.SessionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_community_session_subscriptions_session");
    }
}
