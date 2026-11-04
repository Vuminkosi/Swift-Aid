using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

public sealed class SessionMaterialApprovalEntityConfiguration : IEntityTypeConfiguration<SessionMaterialApprovalEntity>
{
    public void Configure(EntityTypeBuilder<SessionMaterialApprovalEntity> builder)
    {
        builder.ToTable("session_material_approvals", table =>
        {
            table.HasCheckConstraint("ck_session_material_approval_ids", "length(btrim(organization_id)) > 0 AND length(btrim(material_id)) > 0 AND length(btrim(source_version)) > 0 AND length(btrim(rights_evidence)) > 0 AND length(btrim(approved_by_actor_id)) > 0");
            table.HasCheckConstraint("ck_session_material_approval_revocation", "(revoked_at_utc IS NULL AND revoked_by_actor_id IS NULL) OR (revoked_at_utc IS NOT NULL AND length(btrim(revoked_by_actor_id)) > 0)");
        });
        builder.HasKey(item => new { item.OrganizationId, item.MaterialId, item.SourceVersion }).HasName("pk_session_material_approvals");
        builder.Property(item => item.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(item => item.OrganizationId).HasColumnName("organization_id").HasColumnType("text");
        builder.Property(item => item.MaterialId).HasColumnName("material_id").HasColumnType("text");
        builder.Property(item => item.SourceVersion).HasColumnName("source_version").HasColumnType("text");
        builder.Property(item => item.RightsEvidence).HasColumnName("rights_evidence").HasColumnType("text");
        builder.Property(item => item.ApprovedByActorId).HasColumnName("approved_by_actor_id").HasColumnType("text");
        builder.Property(item => item.ApprovedAtUtc).HasColumnName("approved_at_utc").HasColumnType("timestamp with time zone");
        builder.Property(item => item.RevokedByActorId).HasColumnName("revoked_by_actor_id").HasColumnType("text");
        builder.Property(item => item.RevokedAtUtc).HasColumnName("revoked_at_utc").HasColumnType("timestamp with time zone");
        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(item => item.OrganizationId)
            .HasPrincipalKey(item => item.OrganizationId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_session_material_approvals_organization");
        builder.HasOne<ActorEntity>().WithMany().HasForeignKey(item => item.ApprovedByActorId)
            .HasPrincipalKey(item => item.ActorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_session_material_approvals_approver");
        builder.HasOne<ActorEntity>().WithMany().HasForeignKey(item => item.RevokedByActorId)
            .HasPrincipalKey(item => item.ActorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_session_material_approvals_revoked_by_actor");
    }
}
