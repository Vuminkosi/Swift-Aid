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

        return await AuthorizeAllAsync(actor, caseId, [operation], cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AccessDecision> AuthorizeAllAsync(
        ActorContext actor,
        Guid caseId,
        IReadOnlyCollection<CaseOperation> requiredOperations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(requiredOperations);

        var operations = requiredOperations
            .Distinct()
            .OrderBy(operation => (int)operation)
            .ToArray();
        if (operations.Length == 0 || operations.Any(operation => !Enum.IsDefined(operation)))
        {
            throw new ArgumentException("At least one defined case operation is required.", nameof(requiredOperations));
        }

        var auditedOperation = operations[0];

        if (caseId == Guid.Empty)
        {
            return Deny(actor, caseId, null, auditedOperation, AccessDecisionCode.InvalidResourceId, actor.AccessEpoch);
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
            return Deny(actor, caseId, null, auditedOperation, AccessDecisionCode.GrantSourceUnavailable, actor.AccessEpoch);
        }

        if (grant is null)
        {
            return Deny(actor, caseId, null, auditedOperation, AccessDecisionCode.GrantMissing, actor.AccessEpoch);
        }

        if (!string.Equals(grant.ActorId, actor.ActorId, StringComparison.Ordinal))
        {
            return Deny(actor, caseId, null, auditedOperation, AccessDecisionCode.GrantActorMismatch, grant.AccessEpoch);
        }

        if (grant.AccessEpoch != actor.AccessEpoch)
        {
            return Deny(actor, caseId, null, auditedOperation, AccessDecisionCode.AccessEpochMismatch, grant.AccessEpoch);
        }

        if (grant.Status != AccessGrantStatus.Active)
        {
            return Deny(actor, caseId, null, auditedOperation, AccessDecisionCode.GrantMissing, grant.AccessEpoch);
        }

        if (grant.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return Deny(actor, caseId, null, auditedOperation, AccessDecisionCode.GrantExpired, grant.AccessEpoch);
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
            return Deny(actor, caseId, null, auditedOperation, AccessDecisionCode.ResourceSourceUnavailable, grant.AccessEpoch);
        }

        if (resource is null)
        {
            return Deny(actor, caseId, null, auditedOperation, AccessDecisionCode.ResourceMissing, grant.AccessEpoch);
        }

        if (resource.CaseId != caseId)
        {
            return Deny(actor, caseId, null, auditedOperation, AccessDecisionCode.ResourceIdMismatch, grant.AccessEpoch);
        }

        var now = _timeProvider.GetUtcNow();
        if (grant.ExpiresAtUtc <= now)
        {
            return Deny(actor, caseId, resource.ResourceVersion, auditedOperation, AccessDecisionCode.GrantExpired, grant.AccessEpoch);
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
            return Deny(actor, caseId, resource.ResourceVersion, auditedOperation, code, grant.AccessEpoch);
        }

        AccessDecisionCode? closestDenial = null;
        var closestMatchedDimensions = -1;
        foreach (var group in validGroups)
        {
            var (denial, matchedDimensions) = EvaluateGroup(group, resource, operations);
            if (denial is null)
            {
                return new AccessDecision(true, AccessDecisionCode.Allowed, null)
                {
                    MatchedGrantGroupId = group.GroupId
                };
            }

            if (matchedDimensions > closestMatchedDimensions)
            {
                closestMatchedDimensions = matchedDimensions;
                closestDenial = denial;
            }
        }

        return Deny(actor, caseId, resource.ResourceVersion, auditedOperation,
            closestDenial ?? AccessDecisionCode.GrantMissing, grant.AccessEpoch);
    }

    private static (AccessDecisionCode? Denial, int MatchedDimensions) EvaluateGroup(
        ScopedAccessGrant group,
        CaseAccessDescriptor resource,
        IReadOnlyCollection<CaseOperation> operations)
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
        if (operations.Any(operation => !group.Operations.Contains(operation)))
        {
            return (AccessDecisionCode.OperationNotGranted, matched);
        }

        matched++;
        var shareAllowsOperations = group.SharedCaseIds.Contains(resource.CaseId) &&
            (!group.SharedCaseOperations.TryGetValue(resource.CaseId, out var sharedOperations) ||
                operations.All(sharedOperations.Contains));
        var explicitlyAssignedOrShared = group.AssignedCaseIds.Contains(resource.CaseId) || shareAllowsOperations;

        if (explicitlyAssignedOrShared)
        {
            return (null, matched + 1);
        }

        // Only jurisdiction-wide access must cover every case region. A direct
        // assignment or explicit share bypasses the region check alone; the
        // organization/workspace/country/sector/sensitivity intersection above
        // still applies inside the same scalar group.
        var coversEveryRegion = resource.RegionIds.Count > 0 &&
            resource.RegionIds.All(group.RegionIds.Contains);
        if (!coversEveryRegion)
        {
            return (AccessDecisionCode.RegionOutsideGrant, matched);
        }

        if (!group.CanAccessJurisdictionalCases)
        {
            return (AccessDecisionCode.CaseNotAssignedOrShared, matched);
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
