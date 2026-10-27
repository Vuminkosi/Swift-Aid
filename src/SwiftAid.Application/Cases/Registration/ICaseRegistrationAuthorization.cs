using SwiftAid.Application.Authorization;

namespace SwiftAid.Application.Cases.Registration;

/// <summary>Lease-bound authorization for trusted staff registration and authorized receipt replay.</summary>
public interface ICaseRegistrationAuthorization
{
    /// <summary>
    /// Authorizes a new managed case from the trusted actor, workspace hint and confirmed occurrence scope.
    /// Implementations require current ReadCase and AcceptReport together in one live grant group under
    /// this active create lease; no caller-supplied role or capability is accepted.
    /// </summary>
    ValueTask<CaseRegistrationAuthorizationDecision> AuthorizeNewScopeAsync(
        ActorContext actor,
        ICaseRegistrationLease lease,
        RegisterManagedCaseRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks current ReadCase and AcceptReport in one live grant group against the existing leased case's
    /// current authoritative scope. Call before loading any receipt contents.
    /// </summary>
    ValueTask<CaseRegistrationAuthorizationDecision> AuthorizeExistingCaseReplayAsync(
        ActorContext actor,
        ICaseRegistrationLease lease,
        CancellationToken cancellationToken);
}

public sealed record CaseRegistrationAuthorizationDecision(
    bool IsAllowed,
    AccessDecisionCode Code,
    string? MatchedGrantGroupId);
