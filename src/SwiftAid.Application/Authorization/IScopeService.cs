using SwiftAid.Contracts.Sync;

namespace SwiftAid.Application.Authorization;

public interface IScopeService
{
    ValueTask<ScopeManifest> GetAsync(ActorContext actor, CancellationToken cancellationToken);
}

public enum ScopeFailureCode
{
    GrantMissing,
    GrantSourceUnavailable,
    GrantActorMismatch,
    AccessEpochMismatch,
    GrantExpired
}

public sealed class ScopeUnavailableException(ScopeFailureCode code)
    : InvalidOperationException("The current authorized scope is unavailable.")
{
    public ScopeFailureCode Code { get; } = code;
}

/// <summary>Builds a minimized scope from an authoritative grant lookup on every call.</summary>
public sealed class ScopeService : IScopeService
{
    private readonly ICurrentAccessGrantStore _grantStore;
    private readonly TimeProvider _timeProvider;

    public ScopeService(ICurrentAccessGrantStore grantStore, TimeProvider? timeProvider = null)
    {
        _grantStore = grantStore ?? throw new ArgumentNullException(nameof(grantStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ScopeManifest> GetAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

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
            throw new ScopeUnavailableException(ScopeFailureCode.GrantSourceUnavailable);
        }

        if (grant is null)
        {
            throw new ScopeUnavailableException(ScopeFailureCode.GrantMissing);
        }

        if (!string.Equals(grant.ActorId, actor.ActorId, StringComparison.Ordinal))
        {
            throw new ScopeUnavailableException(ScopeFailureCode.GrantActorMismatch);
        }

        if (grant.AccessEpoch != actor.AccessEpoch)
        {
            throw new ScopeUnavailableException(ScopeFailureCode.AccessEpochMismatch);
        }

        if (grant.Status != AccessGrantStatus.Active)
        {
            throw new ScopeUnavailableException(ScopeFailureCode.GrantMissing);
        }

        if (grant.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            throw new ScopeUnavailableException(ScopeFailureCode.GrantExpired);
        }

        var now = _timeProvider.GetUtcNow();
        var validGroups = grant.Grants
            .Where(group => group.ValidFromUtc <= now && now < group.ExpiresAtUtc)
            .OrderBy(group => group.GroupId, StringComparer.Ordinal)
            .SelectMany(group => ProjectGroup(group, grant.ExpiresAtUtc))
            .ToArray();

        if (validGroups.Length == 0)
        {
            throw new ScopeUnavailableException(grant.Grants.Count == 0
                ? ScopeFailureCode.GrantMissing
                : ScopeFailureCode.GrantExpired);
        }

        return new ScopeManifest(validGroups, grant.AccessEpoch, grant.ExpiresAtUtc);
    }

    private static IEnumerable<ScopeGrantManifest> ProjectGroup(ScopedAccessGrant group, DateTimeOffset topExpiry)
    {
        ScopeGrantManifest Create(string id, IEnumerable<Guid> cases, IEnumerable<CaseOperation> operations, bool jurisdiction) => new(
            id, group.ValidFromUtc, group.ExpiresAtUtc < topExpiry ? group.ExpiresAtUtc : topExpiry,
            group.OrganizationIds, group.WorkspaceIds, cases, group.CountryCodes, group.RegionIds,
            group.SensitivityCodes, group.Sectors.Select(sector => sector.ToString()),
            operations.Select(operation => operation.ToString()), jurisdiction);

        yield return Create(group.GroupId,
            group.AssignedCaseIds.Concat(group.SharedCaseIds.Where(id => !group.SharedCaseOperations.ContainsKey(id))),
            group.Operations, group.CanAccessJurisdictionalCases);
        foreach (var share in group.SharedCaseOperations.OrderBy(pair => pair.Key))
        {
            // Separate manifest entries keep a collaborator's case and actions correlated.
            yield return Create($"{group.GroupId}:share:{share.Key:D}", [share.Key],
                group.Operations.Intersect(share.Value), false);
        }
    }
}
