using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Entities.Cases;

public sealed class ManagedCaseObservationEntityConfiguration : IEntityTypeConfiguration<ManagedCaseObservationEntity>
{
    public void Configure(EntityTypeBuilder<ManagedCaseObservationEntity> builder)
    {
        builder.ToTable("managed_case_observations", table =>
        {
            table.HasCheckConstraint("ck_managed_case_observations_text", "length(btrim(narrative)) > 0 AND length(btrim(source)) > 0");
            table.HasCheckConstraint("ck_managed_case_observations_version", "aggregate_version > 0");
        });
        builder.HasKey(item => item.ObservationId).HasName("pk_managed_case_observations");
        builder.Property(item => item.ObservationId).HasColumnName("observation_id").HasColumnType("uuid");
        builder.Property(item => item.CaseId).HasColumnName("case_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.Narrative).HasColumnName("narrative").HasColumnType("text").IsRequired();
        builder.Property(item => item.Source).HasColumnName("source").HasColumnType("text").IsRequired();
        builder.Property(item => item.AggregateVersion).HasColumnName("aggregate_version").HasColumnType("bigint").IsRequired();
        builder.HasIndex(item => item.CaseId).HasDatabaseName("ix_managed_case_observations_case");
        builder.HasOne<ManagedCaseEntity>().WithMany().HasForeignKey(item => item.CaseId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_managed_case_observations_case");
    }
}
