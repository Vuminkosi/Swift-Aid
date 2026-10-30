using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

public sealed class EllaiTopicEntityConfiguration : IEntityTypeConfiguration<EllaiTopicEntity>
{
    public void Configure(EntityTypeBuilder<EllaiTopicEntity> builder)
    {
        builder.ToTable("ellai_topics", table =>
        {
            table.HasCheckConstraint(
                "ck_ellai_topics_ids_nonempty",
                "length(btrim(topic_id)) > 0 AND length(btrim(session_id)) > 0 " +
                "AND length(btrim(title)) > 0");
        });

        builder.HasKey(entity => entity.TopicId).HasName("pk_ellai_topics");
        builder.HasIndex(entity => entity.SessionId).HasDatabaseName("ix_ellai_topics_session");
        builder.Property(entity => entity.TopicId).HasColumnName("topic_id").HasColumnType("text");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id").HasColumnType("text");
        builder.Property(entity => entity.Title).HasColumnName("title").HasColumnType("text");
        builder.Property(entity => entity.AliasesJson).HasColumnName("aliases_json").HasColumnType("text");
        builder.Property(entity => entity.EntityRef).HasColumnName("entity_ref").HasColumnType("text");
        builder.Property(entity => entity.EntityVersion).HasColumnName("entity_version").HasColumnType("text");
        builder.Property(entity => entity.CoverageState).HasColumnName("coverage_state").HasColumnType("text");
        builder.Property(entity => entity.CheckpointId).HasColumnName("checkpoint_id").HasColumnType("text");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
    }
}
