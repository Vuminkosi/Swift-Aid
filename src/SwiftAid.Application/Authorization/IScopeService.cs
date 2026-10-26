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

        if (grant.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            throw new ScopeUnavailableException(ScopeFailureCode.GrantExpired);
        }

        return new ScopeManifest(
            grant.OrganizationIds,
            grant.WorkspaceIds,
            grant.AssignedCaseIds.Concat(grant.SharedCaseIds),
            grant.CountryCodes,
            grant.RegionIds,
            grant.AccessEpoch,
            grant.ExpiresAtUtc);
    }
}
