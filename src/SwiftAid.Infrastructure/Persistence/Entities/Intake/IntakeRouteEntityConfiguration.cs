using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

public sealed class IntakeRouteEntityConfiguration : IEntityTypeConfiguration<IntakeRouteEntity>
{
    public void Configure(EntityTypeBuilder<IntakeRouteEntity> builder)
    {
        builder.ToTable("intake_routes", "swiftaid_intake", table =>
        {
            table.HasCheckConstraint("ck_intake_routes_country", "length(country_code) = 2 AND country_code = upper(country_code)");
            table.HasCheckConstraint("ck_intake_routes_ids", "length(btrim(catalogue_version)) > 0 AND length(btrim(receiving_organization_id)) > 0 AND length(btrim(intake_queue_id)) > 0");
            table.HasCheckConstraint("ck_intake_routes_validity", "expires_at_utc IS NULL OR expires_at_utc > effective_from_utc");
            table.HasCheckConstraint("ck_intake_routes_version", "version > 0");
        });
        builder.HasKey(item => item.RouteId).HasName("pk_intake_routes");
        builder.Property(item => item.RouteId).HasColumnName("route_id").HasColumnType("uuid");
        builder.Property(item => item.CountryCode).HasColumnName("country_code").HasColumnType("text").IsRequired();
        builder.Property(item => item.CatalogueVersion).HasColumnName("catalogue_version").HasColumnType("text").IsRequired();
        builder.Property(item => item.RegionId).HasColumnName("region_id").HasColumnType("text");
        builder.Property(item => item.AreaId).HasColumnName("area_id").HasColumnType("text");
        builder.Property(item => item.ReceivingOrganizationId).HasColumnName("receiving_organization_id").HasColumnType("text").IsRequired();
        builder.Property(item => item.IntakeQueueId).HasColumnName("intake_queue_id").HasColumnType("text").IsRequired();
        builder.Property(item => item.EffectiveFromUtc).HasColumnName("effective_from_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(item => item.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(item => item.IsActive).HasColumnName("is_active").HasColumnType("boolean").IsRequired();
        builder.Property(item => item.Version).HasColumnName("version").HasColumnType("bigint").IsRequired();
        builder.HasIndex(item => new { item.CountryCode, item.CatalogueVersion, item.RegionId, item.AreaId, item.IsActive })
            .HasDatabaseName("ix_intake_routes_match");
        builder.HasIndex(item => item.ReceivingOrganizationId).HasDatabaseName("ix_intake_routes_organization");
        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(item => item.ReceivingOrganizationId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_intake_routes_organization");
    }
}
