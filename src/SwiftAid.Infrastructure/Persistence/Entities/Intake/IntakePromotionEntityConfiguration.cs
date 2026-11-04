using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

public sealed class IntakePromotionEntityConfiguration : IEntityTypeConfiguration<IntakePromotionEntity>
{
    public void Configure(EntityTypeBuilder<IntakePromotionEntity> builder)
    {
        builder.ToTable("intake_promotions", "swiftaid_intake", table =>
        {
            table.HasCheckConstraint("ck_intake_promotions_revision", "source_revision > 0");
        });
        builder.HasKey(item => item.ReceiptId).HasName("pk_intake_promotions");
        builder.Property(item => item.ReceiptId).HasColumnName("receipt_id").HasColumnType("uuid");
        builder.Property(item => item.CaseId).HasColumnName("case_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.AcceptingOfficerActorId).HasColumnName("accepting_officer_actor_id").HasColumnType("text").IsRequired();
        builder.Property(item => item.SourceRevision).HasColumnName("source_revision").HasColumnType("bigint").IsRequired();
        builder.Property(item => item.InitialAssigneeActorId).HasColumnName("initial_assignee_actor_id").HasColumnType("text");
        builder.Property(item => item.PromotedAtUtc).HasColumnName("promoted_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(item => item.CaseId).IsUnique().HasDatabaseName("ux_intake_promotions_case");
        builder.HasOne<IntakeReportEntity>().WithMany().HasForeignKey(item => item.ReceiptId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_intake_promotions_report");
    }
}
