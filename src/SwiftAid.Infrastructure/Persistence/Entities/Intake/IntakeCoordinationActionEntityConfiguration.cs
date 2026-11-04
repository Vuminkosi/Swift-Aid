using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

public sealed class IntakeCoordinationActionEntityConfiguration : IEntityTypeConfiguration<IntakeCoordinationActionEntity>
{
    public void Configure(EntityTypeBuilder<IntakeCoordinationActionEntity> builder)
    {
        builder.ToTable("intake_coordination_actions", "swiftaid_intake", table =>
        {
            table.HasCheckConstraint("ck_intake_coordination_action", "action IN ('ClaimForReview', 'AssignQueue', 'RequestClarification', 'RecordNoServiceCoverage')");
            table.HasCheckConstraint("ck_intake_coordination_actor", "length(btrim(actor_id)) > 0 AND length(btrim(coordination_organization_id)) > 0 AND length(btrim(grant_group_id)) > 0");
            table.HasCheckConstraint("ck_intake_coordination_version", "resulting_version > 0");
        });
        builder.HasKey(item => item.ActionId).HasName("pk_intake_coordination_actions");
        builder.Property(item => item.ActionId).HasColumnName("action_id").HasColumnType("uuid");
        builder.Property(item => item.ReceiptId).HasColumnName("receipt_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.Action).HasColumnName("action").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(item => item.ActorId).HasColumnName("actor_id").HasColumnType("text").IsRequired();
        builder.Property(item => item.CoordinationOrganizationId).HasColumnName("coordination_organization_id").HasColumnType("text").IsRequired();
        builder.Property(item => item.GrantGroupId).HasColumnName("grant_group_id").HasColumnType("text").IsRequired();
        builder.Property(item => item.Reason).HasColumnName("reason").HasColumnType("text");
        builder.Property(item => item.FromStatus).HasColumnName("from_status").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(item => item.FromRouting).HasColumnName("from_routing").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(item => item.FromQueueId).HasColumnName("from_queue_id").HasColumnType("text");
        builder.Property(item => item.ToStatus).HasColumnName("to_status").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(item => item.ToRouting).HasColumnName("to_routing").HasConversion<string>().HasColumnType("text").IsRequired();
        builder.Property(item => item.ToQueueId).HasColumnName("to_queue_id").HasColumnType("text");
        builder.Property(item => item.ResultingVersion).HasColumnName("resulting_version").HasColumnType("bigint").IsRequired();
        builder.Property(item => item.ActedAtUtc).HasColumnName("acted_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(item => new { item.ReceiptId, item.ActedAtUtc }).HasDatabaseName("ix_intake_coordination_actions_receipt");
        builder.HasOne<IntakeReportEntity>().WithMany().HasForeignKey(item => item.ReceiptId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_intake_coordination_actions_report");
    }
}
