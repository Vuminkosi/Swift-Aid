using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Ellai;

public sealed class AgentInvestigationEntity
{
    public string SessionId { get; set; } = "";
    public string OwnerActorId { get; set; } = "";
    public string ProtectedRequest { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AgentInvestigationConfiguration : IEntityTypeConfiguration<AgentInvestigationEntity>
{
    public void Configure(EntityTypeBuilder<AgentInvestigationEntity> builder)
    {
        builder.ToTable("agent_investigations");
        builder.HasKey(row => row.SessionId);
        builder.Property(row => row.SessionId).HasColumnName("session_id");
        builder.Property(row => row.OwnerActorId).HasColumnName("owner_actor_id");
        builder.Property(row => row.ProtectedRequest).HasColumnName("protected_request");
        builder.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.HasIndex(row => new { row.OwnerActorId, row.CreatedAtUtc });
        builder.HasOne<EllaiSessionEntity>().WithOne().HasForeignKey<AgentInvestigationEntity>(row => row.SessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ActorEntity>().WithMany().HasForeignKey(row => row.OwnerActorId).OnDelete(DeleteBehavior.Restrict);
    }
}