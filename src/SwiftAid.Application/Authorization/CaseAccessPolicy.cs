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

        if (grant.Status != AccessGrantStatus.Active)
        {
            return Deny(actor, caseId, null, operation, AccessDecisionCode.GrantMissing, grant.AccessEpoch);
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

        var now = _timeProvider.GetUtcNow();
        if (grant.ExpiresAtUtc <= now)
        {
            return Deny(actor, caseId, resource.ResourceVersion, operation, AccessDecisionCode.GrantExpired, grant.AccessEpoch);
        }

        var validGroups = grant.Grants
            .Where(group => group.ValidFromUtc <= now && now < group.ExpiresAtUtc)
            .OrderBy(group => group.GroupId, StringComparer.Ordinal)
            .ToArray();

        if (validGroups.Length == 0)
        {
            var code = grant.Grants.Count == 0
                ? AccessDecisionCode.GrantMissing
                : AccessDecisionCode.GrantExpired;
            return Deny(actor, caseId, resource.ResourceVersion, operation, code, grant.AccessEpoch);
        }

        AccessDecisionCode? closestDenial = null;
        var closestMatchedDimensions = -1;
        foreach (var group in validGroups)
        {
            var (denial, matchedDimensions) = EvaluateGroup(group, resource, operation);
            if (denial is null)
            {
                return new AccessDecision(true, AccessDecisionCode.Allowed, null);
            }

            if (matchedDimensions > closestMatchedDimensions)
            {
                closestMatchedDimensions = matchedDimensions;
                closestDenial = denial;
            }
        }

        return Deny(actor, caseId, resource.ResourceVersion, operation,
            closestDenial ?? AccessDecisionCode.GrantMissing, grant.AccessEpoch);
    }

    private static (AccessDecisionCode? Denial, int MatchedDimensions) EvaluateGroup(
        ScopedAccessGrant group,
        CaseAccessDescriptor resource,
        CaseOperation operation)
    {
        var matched = 0;
        if (!group.OrganizationIds.Contains(resource.OrganizationId))
        {
            return (AccessDecisionCode.OrganizationOutsideGrant, matched);
        }

        matched++;
        if (!group.WorkspaceIds.Contains(resource.WorkspaceId))
        {
            return (AccessDecisionCode.WorkspaceOutsideGrant, matched);
        }

        matched++;
        if (!group.CountryCodes.Contains(resource.CountryCode))
        {
            return (AccessDecisionCode.CountryOutsideGrant, matched);
        }

        matched++;
        if (!group.Sectors.Contains(resource.Sector))
        {
            return (AccessDecisionCode.SectorOutsideGrant, matched);
        }

        matched++;
        if (!group.SensitivityCodes.Contains(resource.SensitivityCode))
        {
            return (AccessDecisionCode.SensitivityOutsideGrant, matched);
        }

        matched++;
        if (!group.Operations.Contains(operation))
        {
            return (AccessDecisionCode.OperationNotGranted, matched);
        }

        matched++;
        var explicitlyAssignedOrShared =
            group.AssignedCaseIds.Contains(resource.CaseId) || group.SharedCaseIds.Contains(resource.CaseId);
        if (explicitlyAssignedOrShared)
        {
            return (null, matched + 1);
        }

        if (!group.CanAccessJurisdictionalCases)
        {
            return (AccessDecisionCode.CaseNotAssignedOrShared, matched);
        }

        var coversEveryRegion = resource.RegionIds.Count > 0 &&
            resource.RegionIds.All(group.RegionIds.Contains);
        if (!coversEveryRegion)
        {
            return (AccessDecisionCode.RegionOutsideGrant, matched + 1);
        }

        return (null, matched + 2);
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
