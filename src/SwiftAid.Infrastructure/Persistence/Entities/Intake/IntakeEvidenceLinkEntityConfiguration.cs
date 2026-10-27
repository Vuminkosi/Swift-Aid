using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

public sealed class IntakeEvidenceLinkEntityConfiguration : IEntityTypeConfiguration<IntakeEvidenceLinkEntity>
{
    public void Configure(EntityTypeBuilder<IntakeEvidenceLinkEntity> builder)
    {
        builder.ToTable("intake_evidence_links", "swiftaid_intake", table =>
        {
            table.HasCheckConstraint("ck_intake_evidence_links_revision", "source_revision > 0");
        });
        builder.HasKey(item => item.ReceiptId).HasName("pk_intake_evidence_links");
        builder.Property(item => item.ReceiptId).HasColumnName("receipt_id").HasColumnType("uuid");
        builder.Property(item => item.CaseId).HasColumnName("case_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.LinkingOfficerActorId).HasColumnName("linking_officer_actor_id").HasColumnType("text").IsRequired();
        builder.Property(item => item.SourceRevision).HasColumnName("source_revision").HasColumnType("bigint").IsRequired();
        builder.Property(item => item.LinkedAtUtc).HasColumnName("linked_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(item => item.CaseId).HasDatabaseName("ix_intake_evidence_links_case");
        builder.HasOne<IntakeReportEntity>().WithMany().HasForeignKey(item => item.ReceiptId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_intake_evidence_links_report");
        builder.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(item => item.CaseId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_intake_evidence_links_case");
    }
}
