using SwiftAid.Contracts.Intake;

namespace SwiftAid.Application.Intake;

/// <summary>
/// Raised when a reviewer accepts an intake report into a managed case before the
/// managed-case registration execution backend (implementation task P5.1 persistence:
/// <c>ICaseRegistrationUnitOfWork</c>, <c>ICaseRegistrationAuthorization</c> and
/// <c>ICaseRegistrationPersistence</c> have no production implementation) can create
/// the <c>CaseReported</c> plus <c>CaseAssigned</c> events atomically with the
/// <c>intake_promotions</c> link. Triage decisions remain fully functional; only the
/// promotion execution path is unavailable. A future staff endpoint maps this to
/// HTTP 503 rather than recording a false promotion.
/// </summary>
public sealed class IntakePromotionUnavailableException()
    : Exception("Intake-to-case promotion requires the managed-case registration execution backend.")
{
}
