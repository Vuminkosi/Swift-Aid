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
            table.HasCheckConstraint("ck_ellai_sessions_cycles", "cycles_used >= 0 AND cycles_allowed >= 1");
            table.HasCheckConstraint(
                "ck_ellai_sessions_ids_nonempty",
                "length(btrim(session_id)) > 0 AND length(btrim(scope_epoch)) > 0");
        });

        builder.HasKey(entity => entity.SessionId).HasName("pk_ellai_sessions");
        builder.HasIndex(entity => entity.Status).HasDatabaseName("ix_ellai_sessions_status");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id").HasColumnType("text");
        builder.Property(entity => entity.OwnerActorId).HasColumnName("owner_actor_id").HasColumnType("text");
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

public sealed class EllaiUtteranceEntityConfiguration : IEntityTypeConfiguration<EllaiUtteranceEntity>
{
    public const int MaximumWorkPayloadBytes = 16 * 1024;

    public void Configure(EntityTypeBuilder<EllaiUtteranceEntity> builder)
    {
        builder.ToTable("ellai_utterances", table =>
        {
            table.HasCheckConstraint(
                "ck_ellai_utterances_ids_nonempty",
                "length(btrim(session_id)) > 0 AND length(btrim(client_utterance_id)) > 0");
            table.HasCheckConstraint(
                "ck_ellai_utterances_attempt_count",
                "attempt_count >= 0");
            table.HasCheckConstraint("ck_ellai_utterances_outcome",
                "outcome IN ('Queued', 'Processing', 'RetryableFailure', 'Completed', 'TerminalFailure', 'Cancelled', 'LegacyUnknown')");
            table.HasCheckConstraint("ck_ellai_utterances_completed_response",
                "outcome <> 'Completed' OR (assistant_text IS NOT NULL AND length(btrim(assistant_text)) > 0 AND completed_at_utc IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_ellai_utterances_payload_size",
                $"work_payload_json IS NULL OR octet_length(work_payload_json::text) <= {MaximumWorkPayloadBytes}");
        });

        builder.HasKey(entity => new { entity.SessionId, entity.ClientUtteranceId })
            .HasName("pk_ellai_utterances");
        builder.HasIndex(entity => entity.SessionId).HasDatabaseName("ix_ellai_utterances_session");
        builder.HasOne<EllaiSessionEntity>().WithMany()
            .HasForeignKey(entity => entity.SessionId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_ellai_utterances_session");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id").HasColumnType("text");
        builder.Property(entity => entity.ClientUtteranceId).HasColumnName("client_utterance_id").HasColumnType("text");
        builder.Property(entity => entity.AcceptedAtUtc).HasColumnName("accepted_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.WorkPayloadJson).HasColumnName("work_payload_json").HasColumnType("jsonb");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.UserText).HasColumnName("user_text").HasColumnType("text");
        builder.Property(entity => entity.AssistantText).HasColumnName("assistant_text").HasColumnType("text");
        builder.Property(entity => entity.Outcome).HasColumnName("outcome").HasColumnType("text").HasDefaultValue("Queued");
        builder.Property(entity => entity.LeaseExpiresAtUtc).HasColumnName("lease_expires_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        builder.Property(entity => entity.CompletedAtUtc).HasColumnName("completed_at_utc").HasColumnType("timestamp with time zone");
        builder.HasIndex(entity => new { entity.CompletedAtUtc, entity.LeaseExpiresAtUtc, entity.AcceptedAtUtc })
            .HasDatabaseName("ix_ellai_utterances_outbox_claim");
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
