using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

public sealed class IntakeCapabilityEntityConfiguration : IEntityTypeConfiguration<IntakeCapabilityEntity>
{
    public void Configure(EntityTypeBuilder<IntakeCapabilityEntity> builder)
    {
        builder.ToTable("intake_capabilities", "swiftaid_intake", table =>
        {
            table.HasCheckConstraint("ck_intake_capabilities_digest", "octet_length(capability_digest) = 32");
            table.HasCheckConstraint("ck_intake_capabilities_expiry", "expires_at_utc > created_at_utc");
        });
        builder.HasKey(item => item.CapabilityId).HasName("pk_intake_capabilities");
        builder.Property(item => item.CapabilityId).HasColumnName("capability_id").HasColumnType("uuid");
        builder.Property(item => item.SessionId).HasColumnName("session_id").HasColumnType("uuid").IsRequired();
        builder.Property(item => item.CapabilityDigest).HasColumnName("capability_digest").HasColumnType("bytea").IsRequired();
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(item => item.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(item => item.RevokedAtUtc).HasColumnName("revoked_at_utc").HasColumnType("timestamp with time zone");
        builder.HasIndex(item => item.CapabilityDigest).IsUnique().HasDatabaseName("ux_intake_capabilities_digest");
        builder.HasIndex(item => new { item.SessionId, item.ExpiresAtUtc }).HasDatabaseName("ix_intake_capabilities_session_expiry");
        builder.HasOne<IntakeSessionEntity>().WithMany().HasForeignKey(item => item.SessionId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_intake_capabilities_session");
    }
}
