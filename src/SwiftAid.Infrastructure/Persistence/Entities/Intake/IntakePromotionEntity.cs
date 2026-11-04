namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

/// <summary>
/// Unique reviewer-promotion link between one intake receipt and its managed case.
/// Written atomically with the first managed-case events (spec §6); a replay of the
/// same receipt resolves to the same case only for a currently authorized reviewer.
/// </summary>
public sealed class IntakePromotionEntity
{
    public Guid ReceiptId { get; set; }
    public Guid CaseId { get; set; }
    public required string AcceptingOfficerActorId { get; set; }
    public long SourceRevision { get; set; }
    public string? InitialAssigneeActorId { get; set; }
    public DateTimeOffset PromotedAtUtc { get; set; }
}
