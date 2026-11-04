namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

/// <summary>Version-bound country and optional administrative-area catchment mapping to a review queue.</summary>
public sealed class IntakeRouteEntity
{
    public Guid RouteId { get; set; }
    public required string CountryCode { get; set; }
    public required string CatalogueVersion { get; set; }
    public string? RegionId { get; set; }
    public string? AreaId { get; set; }
    public required string ReceivingOrganizationId { get; set; }
    public required string IntakeQueueId { get; set; }
    public DateTimeOffset EffectiveFromUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; }
    public long Version { get; set; }
}
