using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

public sealed class IntakeSessionEntityConfiguration : IEntityTypeConfiguration<IntakeSessionEntity>
{
    public void Configure(EntityTypeBuilder<IntakeSessionEntity> builder)
    {
        builder.ToTable("intake_sessions", "swiftaid_intake", table => table.HasCheckConstraint(
            "ck_intake_sessions_proof_digest", "octet_length(receipt_proof_digest) = 32"));
        builder.HasKey(item => item.SessionId).HasName("pk_intake_sessions");
        builder.Property(item => item.SessionId).HasColumnName("session_id").HasColumnType("uuid");
        builder.Property(item => item.ReceiptProofDigest).HasColumnName("receipt_proof_digest").HasColumnType("bytea").IsRequired();
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(item => item.LastSeenAtUtc).HasColumnName("last_seen_at_utc").HasColumnType("timestamp with time zone").IsRequired();
    }
}
