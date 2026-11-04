using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

public sealed class GeographicLocationEntityConfiguration : IEntityTypeConfiguration<GeographicLocationEntity>
{
    public void Configure(EntityTypeBuilder<GeographicLocationEntity> builder)
    {
        builder.ToTable("locations", "swiftaid_geo", table =>
        {
            table.HasCheckConstraint(
                "ck_geo_locations_ids_nonempty",
                "length(btrim(location_id)) > 0 AND length(btrim(name)) > 0 AND length(btrim(country_code)) = 2");
            table.HasCheckConstraint(
                "ck_geo_locations_kind",
                "location_kind IN ('Country', 'Region', 'Province', 'Settlement')");
        });
        builder.HasKey(item => item.LocationId).HasName("pk_geo_locations");
        builder.Property(item => item.LocationId).HasColumnName("location_id").HasColumnType("text").IsRequired();
        builder.Property(item => item.CountryCode).HasColumnName("country_code").HasColumnType("text").IsRequired();
        builder.Property(item => item.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        builder.Property(item => item.LocationKind).HasColumnName("location_kind").HasColumnType("text").IsRequired();
        builder.Property(item => item.ParentId).HasColumnName("parent_id").HasColumnType("text");
        builder.Property(item => item.Latitude).HasColumnName("latitude").HasColumnType("double precision");
        builder.Property(item => item.Longitude).HasColumnName("longitude").HasColumnType("double precision");
        builder.Property(item => item.Source).HasColumnName("source").HasColumnType("text").IsRequired();
        builder.Property(item => item.Verification).HasColumnName("verification").HasColumnType("text").IsRequired();
        builder.Property(item => item.IsActive).HasColumnName("is_active");
        builder.Property(item => item.ImportedAtUtc).HasColumnName("imported_at_utc")
            .HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(item => new { item.CountryCode, item.Name }).HasDatabaseName("ix_geo_locations_country_name");
        builder.HasIndex(item => item.ParentId).HasDatabaseName("ix_geo_locations_parent");
        builder.HasOne<GeographicLocationEntity>().WithMany().HasForeignKey(item => item.ParentId)
            .HasPrincipalKey(item => item.LocationId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_geo_locations_parent");
    }
}
