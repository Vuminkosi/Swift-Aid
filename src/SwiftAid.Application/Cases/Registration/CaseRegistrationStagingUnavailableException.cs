namespace SwiftAid.Application.Cases.Registration;

/// <summary>
/// Raised when managed-case journal staging is invoked before the registration event
/// staging slice lands. Lease lifecycle, authorization, receipt lookup, and denial audit
/// are fully implemented; only <c>StageAcceptedRegistrationAsync</c> (journal hash chain,
/// protected payloads, projections, command receipts) remains. A future staff endpoint
/// maps this to HTTP 503 rather than recording a partial case.
/// </summary>
public sealed class CaseRegistrationStagingUnavailableException()
    : Exception("Managed-case journal staging is not implemented yet.")
{
}
