namespace SwiftAid.Intelligence.Usage;

/// <summary>An actorless request from an authenticated application flow.</summary>
public sealed record ReserveTransferRequest(
    UsagePurpose TargetPurpose,
    decimal AmountUsd,
    string Reason);

/// <summary>
/// An authorization result derived by the trusted application session/policy adapter, not from request data.
/// </summary>
public sealed record AuthorizedReserveTransfer(
    string ApproverId,
    UsagePurpose TargetPurpose,
    decimal AmountUsd,
    string Reason);

/// <summary>
/// Implementations must obtain the approver from authoritative authenticated session context and enforce the
/// transfer permission. They must not trust an actor identifier supplied by the request.
/// </summary>
public interface IUsageBudgetTransferAuthorizer
{
    ValueTask<AuthorizedReserveTransfer?> AuthorizeAsync(
        ReserveTransferRequest request,
        CancellationToken cancellationToken);
}
