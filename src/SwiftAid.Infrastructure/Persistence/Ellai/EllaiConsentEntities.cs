using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

/// <summary>Durable consent-grant row. Grants are immutable except the
/// revocation flag; the service merges rows into its hot path by hydration.
/// No foreign key to sessions: consent outlives session-row churn and must
/// never block session cleanup.</summary>
public sealed class EllaiConsentGrantEntity
{
    public string GrantId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Dimension { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string DataClass { get; set; } = "";
    public int Version { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset GrantedAtUtc { get; set; }
}

public sealed class EllaiConsentGrantEntityConfiguration : IEntityTypeConfiguration<EllaiConsentGrantEntity>
{
    public void Configure(EntityTypeBuilder<EllaiConsentGrantEntity> builder)
    {
        builder.ToTable("ellai_consent_grants", table =>
        {
            table.HasCheckConstraint(
                "ck_ellai_consent_grants_ids_nonempty",
                "length(btrim(grant_id)) > 0 AND length(btrim(session_id)) > 0 " +
                "AND length(btrim(purpose)) > 0");
        });

        builder.HasKey(entity => entity.GrantId).HasName("pk_ellai_consent_grants");
        builder.HasIndex(entity => entity.SessionId).HasDatabaseName("ix_ellai_consent_grants_session");
        builder.Property(entity => entity.GrantId).HasColumnName("grant_id").HasColumnType("text");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id").HasColumnType("text");
        builder.Property(entity => entity.Dimension).HasColumnName("dimension").HasColumnType("text");
        builder.Property(entity => entity.Purpose).HasColumnName("purpose").HasColumnType("text");
        builder.Property(entity => entity.DataClass).HasColumnName("data_class").HasColumnType("text");
        builder.Property(entity => entity.Version).HasColumnName("version");
        builder.Property(entity => entity.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.Revoked).HasColumnName("revoked");
        builder.Property(entity => entity.GrantedAtUtc).HasColumnName("granted_at_utc").HasColumnType("timestamp with time zone");
    }
}
