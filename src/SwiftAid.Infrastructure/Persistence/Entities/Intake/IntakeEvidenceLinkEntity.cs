namespace SwiftAid.Infrastructure.Persistence.Entities.Intake;

/// <summary>
/// Reviewer-recorded link from one intake receipt to the managed case that carries its
/// evidence. Written atomically with the <c>LinkedAsEvidence</c> decision; a replay of the
/// same receipt resolves to the same case, and a different target conflicts.
/// </summary>
public sealed class IntakeEvidenceLinkEntity
{
    public Guid ReceiptId { get; set; }
    public Guid CaseId { get; set; }
    public required string LinkingOfficerActorId { get; set; }
    public long SourceRevision { get; set; }
    public DateTimeOffset LinkedAtUtc { get; set; }
}
