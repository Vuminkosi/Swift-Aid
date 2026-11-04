using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence;

/// <summary>Administrative profile and role metadata. Never a case authorization grant.</summary>
public sealed class OrganizationAdministrationEntity
{
    public string OrganizationId { get; set; } = "";
    public string ProtectedState { get; set; } = "";
    public long Version { get; set; }
}

public sealed class OrganizationAdministrationConfiguration : IEntityTypeConfiguration<OrganizationAdministrationEntity>
{
    public void Configure(EntityTypeBuilder<OrganizationAdministrationEntity> builder)
    {
        builder.ToTable("organization_administration", table => table.HasCheckConstraint("ck_organization_administration_version", "version > 0"));
        builder.HasKey(row => row.OrganizationId);
        builder.Property(row => row.OrganizationId).HasColumnName("organization_id");
        builder.Property(row => row.ProtectedState).HasColumnName("protected_state");
        builder.Property(row => row.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasOne<OrganizationEntity>().WithOne().HasForeignKey<OrganizationAdministrationEntity>(row => row.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}