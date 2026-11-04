namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>Current owner approval for one exact session material version.</summary>
public sealed class SessionMaterialApprovalEntity
{
    public required string OrganizationId { get; set; }
    public required string MaterialId { get; set; }
    public required string SourceVersion { get; set; }
    public required string RightsEvidence { get; set; }
    public required string ApprovedByActorId { get; set; }
    public DateTimeOffset ApprovedAtUtc { get; set; }
    public string? RevokedByActorId { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public long Version { get; set; } = 1;
}
