using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Entities.Cases;

public sealed class ManagedCaseAssistanceEntityConfiguration : IEntityTypeConfiguration<ManagedCaseAssistanceEntity>
{
    public void Configure(EntityTypeBuilder<ManagedCaseAssistanceEntity> builder)
    {
        builder.ToTable("managed_case_assistance", table =>
        {
            table.HasCheckConstraint("ck_managed_case_assistance_status", "status IN ('Requested', 'Accepted', 'InProgress', 'Provided', 'ReceiptConfirmed', 'Unfulfilled', 'Cancelled')");
            table.HasCheckConstraint("ck_managed_case_assistance_values", "length(btrim(need)) > 0 AND length(btrim(responsible_actor_id)) > 0 AND length(btrim(requested_by_actor_id)) > 0 AND length(btrim(last_note)) > 0");
            table.HasCheckConstraint("ck_managed_case_assistance_versions", "last_event_version > 0");
            table.HasCheckConstraint("ck_managed_case_assistance_receipt", "(status = 'ReceiptConfirmed' AND receipt_recorded_by_actor_id IS NOT NULL AND receipt_method IS NOT NULL AND receipt_context IS NOT NULL) OR (status <> 'ReceiptConfirmed' AND receipt_recorded_by_actor_id IS NULL AND receipt_method IS NULL AND receipt_context IS NULL)");
        });
        builder.HasKey(item => item.AssistanceId).HasName("pk_managed_case_assistance");
        builder.Property(item => item.AssistanceId).HasColumnName("assistance_id").HasColumnType("uuid");
        builder.Property(item => item.CaseId).HasColumnName("case_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.Need).HasColumnName("need").HasColumnType("text").IsRequired();
        builder.Property(item => item.ResponsibleActorId).HasColumnName("responsible_actor_id").HasColumnType("text").IsRequired();
        builder.Property(item => item.RequestedByActorId).HasColumnName("requested_by_actor_id").HasColumnType("text").IsRequired();
        builder.Property(item => item.RequestedAtUtc).HasColumnName("requested_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(item => item.DueAtUtc).HasColumnName("due_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(item => item.Status).HasColumnName("status").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(item => item.LastNote).HasColumnName("last_note").HasColumnType("text").IsRequired();
        builder.Property(item => item.AcceptedByActorId).HasColumnName("accepted_by_actor_id").HasColumnType("text");
        builder.Property(item => item.ProvidedByActorId).HasColumnName("provided_by_actor_id").HasColumnType("text");
        builder.Property(item => item.ReceiptRecordedByActorId).HasColumnName("receipt_recorded_by_actor_id").HasColumnType("text");
        builder.Property(item => item.ReceiptMethod).HasColumnName("receipt_method").HasColumnType("text");
        builder.Property(item => item.ReceiptContext).HasColumnName("receipt_context").HasColumnType("text");
        builder.Property(item => item.LastEventVersion).HasColumnName("last_event_version").HasColumnType("bigint").IsRequired();
        builder.HasIndex(item => item.CaseId).HasDatabaseName("ix_managed_case_assistance_case");
        builder.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(item => item.CaseId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_managed_case_assistance_case");
    }
}
