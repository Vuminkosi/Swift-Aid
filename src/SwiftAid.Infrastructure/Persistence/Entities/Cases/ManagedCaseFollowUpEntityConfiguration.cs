using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Entities.Cases;

public sealed class ManagedCaseFollowUpEntityConfiguration : IEntityTypeConfiguration<ManagedCaseFollowUpEntity>
{
    public void Configure(EntityTypeBuilder<ManagedCaseFollowUpEntity> builder)
    {
        builder.ToTable("managed_case_follow_ups", table =>
        {
            table.HasCheckConstraint("ck_managed_case_follow_ups_status", "status IN ('Scheduled', 'Completed', 'Missed', 'Unreachable', 'Cancelled', 'Acknowledged')");
            table.HasCheckConstraint("ck_managed_case_follow_ups_text", "length(btrim(purpose)) > 0 AND length(btrim(assignee_actor_id)) > 0");
            table.HasCheckConstraint("ck_managed_case_follow_ups_versions", "scheduled_version > 0 AND (outcome_version IS NULL OR outcome_version > 0)");
        });
        builder.HasKey(item => item.FollowUpId).HasName("pk_managed_case_follow_ups");
        builder.Property(item => item.FollowUpId).HasColumnName("follow_up_id").HasColumnType("uuid");
        builder.Property(item => item.CaseId).HasColumnName("case_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.Purpose).HasColumnName("purpose").HasColumnType("text").IsRequired();
        builder.Property(item => item.AssigneeActorId).HasColumnName("assignee_actor_id").HasColumnType("text").IsRequired();
        builder.Property(item => item.DueAtUtc).HasColumnName("due_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(item => item.Status).HasColumnName("status").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(item => item.ScheduledVersion).HasColumnName("scheduled_version").HasColumnType("bigint").IsRequired();
        builder.Property(item => item.OutcomeVersion).HasColumnName("outcome_version").HasColumnType("bigint");
        builder.HasIndex(item => item.CaseId).HasDatabaseName("ix_managed_case_follow_ups_case");
        builder.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(item => item.CaseId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_managed_case_follow_ups_case");
    }
}
