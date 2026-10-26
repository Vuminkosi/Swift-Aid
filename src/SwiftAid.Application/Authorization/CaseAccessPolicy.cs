namespace SwiftAid.Application.Authorization;

/// <summary>Applies current identity, grant, jurisdiction and resource constraints together.</summary>
public sealed class CaseAccessPolicy : ICaseAccessPolicy
{
    private readonly ICurrentAccessGrantStore _grantStore;
    private readonly ICaseAccessDescriptorStore _descriptorStore;
    private readonly TimeProvider _timeProvider;

    public CaseAccessPolicy(
        ICurrentAccessGrantStore grantStore,
        ICaseAccessDescriptorStore descriptorStore,
        TimeProvider? timeProvider = null)
    {
        _grantStore = grantStore ?? throw new ArgumentNullException(nameof(grantStore));
        _descriptorStore = descriptorStore ?? throw new ArgumentNullException(nameof(descriptorStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<AccessDecision> AuthorizeAsync(
        ActorContext actor,
        Guid caseId,
        CaseOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!Enum.IsDefined(operation))
        {
            return Deny(actor, caseId, null, operation, AccessDecisionCode.OperationNotGranted, actor.AccessEpoch);
        }

        if (caseId == Guid.Empty)
        {
            return Deny(actor, caseId, null, operation, AccessDecisionCode.InvalidResourceId, actor.AccessEpoch);
        }

        CurrentAccessGrant? grant;
        try
        {
            grant = await _grantStore.GetCurrentGrantAsync(actor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Deny(actor, caseId, null, operation, AccessDecisionCode.GrantSourceUnavailable, actor.AccessEpoch);
        }

        if (grant is null)
        {
            return Deny(actor, caseId, null, operation, AccessDecisionCode.GrantMissing, actor.AccessEpoch);
        }

        if (!string.Equals(grant.ActorId, actor.ActorId, StringComparison.Ordinal))
        {
            return Deny(actor, caseId, null, operation, AccessDecisionCode.GrantActorMismatch, grant.AccessEpoch);
        }

        if (grant.AccessEpoch != actor.AccessEpoch)
        {
            return Deny(actor, caseId, null, operation, AccessDecisionCode.AccessEpochMismatch, grant.AccessEpoch);
        }

        if (grant.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return Deny(actor, caseId, null, operation, AccessDecisionCode.GrantExpired, grant.AccessEpoch);
        }

        CaseAccessDescriptor? resource;
        try
        {
            resource = await _descriptorStore.GetCurrentDescriptorAsync(caseId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Deny(actor, caseId, null, operation, AccessDecisionCode.ResourceSourceUnavailable, grant.AccessEpoch);
        }

        if (resource is null)
        {
            return Deny(actor, caseId, null, operation, AccessDecisionCode.ResourceMissing, grant.AccessEpoch);
        }

        if (resource.CaseId != caseId)
        {
            return Deny(actor, caseId, null, operation, AccessDecisionCode.ResourceIdMismatch, grant.AccessEpoch);
        }

        if (!grant.OrganizationIds.Contains(resource.OrganizationId))
        {
            return Deny(actor, caseId, resource.ResourceVersion, operation, AccessDecisionCode.OrganizationOutsideGrant, grant.AccessEpoch);
        }

        if (!grant.WorkspaceIds.Contains(resource.WorkspaceId))
        {
            return Deny(actor, caseId, resource.ResourceVersion, operation, AccessDecisionCode.WorkspaceOutsideGrant, grant.AccessEpoch);
        }

        if (!grant.CountryCodes.Contains(resource.CountryCode))
        {
            return Deny(actor, caseId, resource.ResourceVersion, operation, AccessDecisionCode.CountryOutsideGrant, grant.AccessEpoch);
        }

        if (!grant.Sectors.Contains(resource.Sector))
        {
            return Deny(actor, caseId, resource.ResourceVersion, operation, AccessDecisionCode.SectorOutsideGrant, grant.AccessEpoch);
        }

        if (!grant.SensitivityCodes.Contains(resource.SensitivityCode))
        {
            return Deny(actor, caseId, resource.ResourceVersion, operation, AccessDecisionCode.SensitivityOutsideGrant, grant.AccessEpoch);
        }

        if (!grant.Operations.Contains(operation))
        {
            return Deny(actor, caseId, resource.ResourceVersion, operation, AccessDecisionCode.OperationNotGranted, grant.AccessEpoch);
        }

        var explicitlyAssignedOrShared =
            grant.AssignedCaseIds.Contains(resource.CaseId) || grant.SharedCaseIds.Contains(resource.CaseId);

        if (!explicitlyAssignedOrShared)
        {
            var coversEveryRegion = resource.RegionIds.Count > 0 &&
                resource.RegionIds.All(grant.RegionIds.Contains);

            if (!grant.CanAccessJurisdictionalCases)
            {
                return Deny(actor, caseId, resource.ResourceVersion, operation, AccessDecisionCode.CaseNotAssignedOrShared, grant.AccessEpoch);
            }

            if (!coversEveryRegion)
            {
                return Deny(actor, caseId, resource.ResourceVersion, operation, AccessDecisionCode.RegionOutsideGrant, grant.AccessEpoch);
            }
        }

        return new AccessDecision(true, AccessDecisionCode.Allowed, null);
    }

    private AccessDecision Deny(
        ActorContext actor,
        Guid resourceId,
        long? resourceVersion,
        CaseOperation operation,
        AccessDecisionCode code,
        long accessEpoch)
    {
        var auditRecord = new AccessAuditRecord(
            actor.ActorId,
            resourceId,
            operation,
            code,
            _timeProvider.GetUtcNow(),
            resourceVersion,
            accessEpoch);

        return new AccessDecision(false, code, auditRecord);
    }
}
