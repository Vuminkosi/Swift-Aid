using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Entities.Cases;

public sealed class ManagedCaseEscalationEntityConfiguration : IEntityTypeConfiguration<ManagedCaseEscalationEntity>
{
    public void Configure(EntityTypeBuilder<ManagedCaseEscalationEntity> builder)
    {
        builder.ToTable("managed_case_escalations", table =>
        {
            table.HasCheckConstraint("ck_managed_case_escalations_status", "status IN ('Raised', 'Acknowledged', 'ActionRecorded', 'Closed')");
            table.HasCheckConstraint("ck_managed_case_escalations_versions", "raised_version > 0 AND (acknowledged_version IS NULL OR acknowledged_version > 0) AND (action_recorded_version IS NULL OR action_recorded_version > 0) AND (closed_version IS NULL OR closed_version > 0)");
        });
        builder.HasKey(item => item.EscalationId).HasName("pk_managed_case_escalations");
        builder.Property(item => item.EscalationId).HasColumnName("escalation_id").HasColumnType("uuid");
        builder.Property(item => item.CaseId).HasColumnName("case_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.Status).HasColumnName("status").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(item => item.RaisedVersion).HasColumnName("raised_version").HasColumnType("bigint").IsRequired();
        builder.Property(item => item.AcknowledgedVersion).HasColumnName("acknowledged_version").HasColumnType("bigint");
        builder.Property(item => item.ActionRecordedVersion).HasColumnName("action_recorded_version").HasColumnType("bigint");
        builder.Property(item => item.ActionSummary).HasColumnName("action_summary").HasColumnType("text");
        builder.Property(item => item.ClosedVersion).HasColumnName("closed_version").HasColumnType("bigint");
        builder.HasIndex(item => item.CaseId).HasDatabaseName("ix_managed_case_escalations_case");
        builder.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(item => item.CaseId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_managed_case_escalations_case");
    }
}
