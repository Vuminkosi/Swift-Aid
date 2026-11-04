using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

public sealed class EllaiPlanApprovalEntityConfiguration : IEntityTypeConfiguration<EllaiPlanApprovalEntity>
{
    public void Configure(EntityTypeBuilder<EllaiPlanApprovalEntity> builder)
    {
        builder.ToTable("ellai_plan_approvals", table =>
        {
            table.HasCheckConstraint(
                "ck_ellai_plan_approvals_ids_nonempty",
                "length(btrim(approval_id)) > 0 AND length(btrim(session_id)) > 0 " +
                "AND length(btrim(action_id)) > 0 AND length(btrim(idempotency_key)) > 0 " +
                "AND length(btrim(plan_hash)) > 0");
            table.HasCheckConstraint(
                "ck_ellai_plan_approvals_status",
                "status IN ('Accepted', 'AcceptedUnverified', 'UnknownOutcome', 'PartiallyApplied', 'Applied', 'Failed')");
            table.HasCheckConstraint(
                "ck_ellai_plan_approvals_expiry",
                "expires_at_utc > created_at_utc");
        });

        builder.HasKey(entity => entity.ApprovalId).HasName("pk_ellai_plan_approvals");
        builder.HasIndex(entity => entity.IdempotencyKey).IsUnique().HasDatabaseName("ux_ellai_plan_approvals_key");
        builder.HasIndex(entity => new { entity.PlanHash, entity.PlanVersion, entity.ActionId })
            .IsUnique().HasDatabaseName("ux_ellai_plan_approvals_plan_action");
        builder.HasIndex(entity => entity.SessionId).HasDatabaseName("ix_ellai_plan_approvals_session");
        builder.Property(entity => entity.ApprovalId).HasColumnName("approval_id").HasColumnType("text");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id").HasColumnType("text");
        builder.Property(entity => entity.ActionId).HasColumnName("action_id").HasColumnType("text");
        builder.Property(entity => entity.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType("text");
        builder.Property(entity => entity.PlanHash).HasColumnName("plan_hash").HasColumnType("text");
        builder.Property(entity => entity.PlanVersion).HasColumnName("plan_version");
        builder.Property(entity => entity.ApprovedRecipient).HasColumnName("approved_recipient").HasColumnType("text");
        builder.Property(entity => entity.ApprovedDueAtUtc).HasColumnName("approved_due_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text");
        builder.Property(entity => entity.PolicyHash).HasColumnName("policy_hash").HasColumnType("text");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.VerifiedAtUtc).HasColumnName("verified_at_utc").HasColumnType("timestamp with time zone");
    }
}
