using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

public sealed class EllaiSessionEntityConfiguration : IEntityTypeConfiguration<EllaiSessionEntity>
{
    public void Configure(EntityTypeBuilder<EllaiSessionEntity> builder)
    {
        builder.ToTable("ellai_sessions", table =>
        {
            table.HasCheckConstraint("ck_ellai_sessions_version", "version > 0");
            table.HasCheckConstraint("ck_ellai_sessions_status", "status IN ('Active', 'Completed', 'Cancelled')");
            table.HasCheckConstraint("ck_ellai_sessions_cycles", "cycles_used >= 0 AND cycles_allowed BETWEEN 1 AND 12");
            table.HasCheckConstraint(
                "ck_ellai_sessions_ids_nonempty",
                "length(btrim(session_id)) > 0 AND length(btrim(scope_epoch)) > 0");
        });

        builder.HasKey(entity => entity.SessionId).HasName("pk_ellai_sessions");
        builder.HasIndex(entity => entity.Status).HasDatabaseName("ix_ellai_sessions_status");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id").HasColumnType("text");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.Mode).HasColumnName("mode").HasColumnType("text");
        builder.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text");
        builder.Property(entity => entity.CyclesUsed).HasColumnName("cycles_used");
        builder.Property(entity => entity.CyclesAllowed).HasColumnName("cycles_allowed");
        builder.Property(entity => entity.ControlFence).HasColumnName("control_fence");
        builder.Property(entity => entity.InteractionEpoch).HasColumnName("interaction_epoch");
        builder.Property(entity => entity.SpeechEpoch).HasColumnName("speech_epoch");
        builder.Property(entity => entity.ScopeEpoch).HasColumnName("scope_epoch").HasColumnType("text");
        builder.Property(entity => entity.ActiveInvestigationId).HasColumnName("active_investigation_id").HasColumnType("text");
        builder.Property(entity => entity.PendingCheckpointId).HasColumnName("pending_checkpoint_id").HasColumnType("text");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone");
    }
}

public sealed class EllaiCheckpointEntityConfiguration : IEntityTypeConfiguration<EllaiCheckpointEntity>
{
    public void Configure(EntityTypeBuilder<EllaiCheckpointEntity> builder)
    {
        builder.ToTable("ellai_checkpoints", table =>
        {
            table.HasCheckConstraint(
                "ck_ellai_checkpoints_ids_nonempty",
                "length(btrim(checkpoint_id)) > 0 AND length(btrim(session_id)) > 0");
        });

        builder.HasKey(entity => entity.CheckpointId).HasName("pk_ellai_checkpoints");
        builder.HasIndex(entity => entity.SessionId).HasDatabaseName("ix_ellai_checkpoints_session");
        builder.HasOne<EllaiSessionEntity>().WithMany()
            .HasForeignKey(entity => entity.SessionId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_ellai_checkpoints_session");
        builder.Property(entity => entity.CheckpointId).HasColumnName("checkpoint_id").HasColumnType("text");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id").HasColumnType("text");
        builder.Property(entity => entity.SessionVersion).HasColumnName("session_version");
        builder.Property(entity => entity.TurnId).HasColumnName("turn_id").HasColumnType("text");
        builder.Property(entity => entity.TopicRefsJson).HasColumnName("topic_refs").HasColumnType("jsonb");
        builder.Property(entity => entity.PendingReceiptIdsJson).HasColumnName("pending_receipt_ids").HasColumnType("jsonb");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
    }
}
