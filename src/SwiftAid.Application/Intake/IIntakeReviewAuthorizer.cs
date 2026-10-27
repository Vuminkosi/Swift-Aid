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
}
