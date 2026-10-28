using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

public sealed class CommunitySessionRevisionEntityConfiguration : IEntityTypeConfiguration<CommunitySessionRevisionEntity>
{
    public void Configure(EntityTypeBuilder<CommunitySessionRevisionEntity> builder)
    {
        builder.ToTable("community_session_revisions", table =>
        {
            table.HasCheckConstraint("ck_community_session_revisions_version", "version > 0");
            table.HasCheckConstraint(
                "ck_community_session_revisions_actor",
                "length(btrim(actor_id)) > 0 AND length(btrim(reason)) > 0");
        });

        builder.HasKey(entity => entity.RevisionId).HasName("pk_community_session_revisions");
        builder.HasIndex(entity => new { entity.SessionId, entity.Version })
            .IsUnique()
            .HasDatabaseName("ux_community_session_revisions_session_version");
        builder.Property(entity => entity.RevisionId).HasColumnName("revision_id").HasColumnType("uuid");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id").HasColumnType("uuid");
        builder.Property(entity => entity.Version).HasColumnName("version");
        builder.Property(entity => entity.ActorId).HasColumnName("actor_id").HasColumnType("text");
        builder.Property(entity => entity.Reason).HasColumnName("reason").HasColumnType("text");
        builder.Property(entity => entity.RecordedAtUtc).HasColumnName("recorded_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.SnapshotJson).HasColumnName("snapshot").HasColumnType("jsonb");
        builder.HasOne<CommunitySessionEntity>().WithMany()
            .HasForeignKey(entity => entity.SessionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_community_session_revisions_session");
    }
}
