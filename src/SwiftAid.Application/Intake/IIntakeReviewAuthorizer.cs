using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Intake;

/// <summary>
/// Determines whether an authenticated staff actor may record a review decision for one
/// intake receipt. The actor must hold the <c>ReviewIntake</c> operation in the report's
/// receiving organization with matching catchment country. Reports without a routed queue
/// have no authorized reviewer through this port.
/// </summary>
public interface IIntakeReviewAuthorizer
{
    Task<bool> IsAuthorizedAsync(ActorContext actor, Guid receiptId, CancellationToken ct);

    /// <summary>
    /// Determines whether the actor may link the receipt's report to the target managed case
    /// as evidence. Requires review authorization plus same-organization containment: the case
    /// must belong to the report's receiving organization so guest evidence cannot cross
    /// organizational boundaries.
    /// </summary>
    Task<bool> IsLinkAuthorizedAsync(ActorContext actor, Guid receiptId, Guid targetCaseId, CancellationToken ct);
}
