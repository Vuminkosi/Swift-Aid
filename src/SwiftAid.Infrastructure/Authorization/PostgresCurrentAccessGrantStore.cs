using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Infrastructure.Authorization;

/// <summary>
/// Reads only the current actor's case-bound authority from the same locked PostgreSQL transaction.
/// This adapter is not a complete session-scope manifest source.
/// </summary>
public sealed class PostgresCurrentAccessGrantStore : ICurrentAccessGrantStore
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly ICaseCommandLeaseAccessor _leaseAccessor;
    private readonly TimeProvider _timeProvider;

    public PostgresCurrentAccessGrantStore(
        SwiftAidDbContext dbContext,
        ICaseCommandLeaseAccessor leaseAccessor,
        TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _leaseAccessor = leaseAccessor ?? throw new ArgumentNullException(nameof(leaseAccessor));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CurrentAccessGrant?> GetCurrentGrantAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var lease = PostgresLeaseGuard.RequireCurrent(_dbContext, _leaseAccessor, actor);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();

        var resource = await _dbContext.ManagedCases
            .AsNoTracking()
            .Where(entity => entity.CaseId == lease.CaseId)
            .Select(entity => new CaseAuthorityScope(
                entity.CaseId,
                entity.OrganizationId,
                entity.WorkspaceId,
                entity.CountryCode,
                entity.Sector,
                entity.SensitivityCode,
                entity.AccountableOwnerActorId))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (resource is null)
        {
            return null;
        }

        var actorEntity = await _dbContext.Actors
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.ActorId == actor.ActorId, cancellationToken)
            .ConfigureAwait(false);
        var accessState = await _dbContext.ActorAccessStates
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.ActorId == actor.ActorId, cancellationToken)
            .ConfigureAwait(false);
        if (actorEntity is null || accessState is null)
        {
            return null;
        }

        var organization = await _dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.OrganizationId == resource.OrganizationId, cancellationToken)
            .ConfigureAwait(false);
        var workspace = await _dbContext.Workspaces
            .AsNoTracking()
            .SingleOrDefaultAsync(entity =>
                entity.WorkspaceId == resource.WorkspaceId && _dbContext.WorkspaceOrganizations.Any(link =>
                    link.WorkspaceId == entity.WorkspaceId && link.OrganizationId == resource.OrganizationId && link.Status == PersistentResourceStatus.Active),
                cancellationToken)
            .ConfigureAwait(false);
        var membership = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .SingleOrDefaultAsync(entity =>
                entity.ActorId == actor.ActorId && entity.OrganizationId == resource.OrganizationId,
                cancellationToken)
            .ConfigureAwait(false);

        var topExpiry = Minimum(
            actorEntity.ExpiresAtUtc ?? DateTimeOffset.MaxValue,
            membership?.ExpiresAtUtc ?? DateTimeOffset.MaxValue);
        var status = DetermineOverallStatus(actorEntity.Status, organization?.Status, workspace?.Status, membership?.Status);
        if (membership is null ||
            membership.ValidFromUtc > now ||
            membership.ExpiresAtUtc is { } membershipExpiry && membershipExpiry <= now)
        {
            status = membership?.Status == PersistentMembershipStatus.Revoked
                ? AccessGrantStatus.Revoked
                : AccessGrantStatus.Suspended;
        }

        var groups = new List<ScopedAccessGrant>();
        if (status == AccessGrantStatus.Active && membership is not null)
        {
            var grantEntities = await _dbContext.AccessGrantGroups
                .AsNoTracking()
                .Where(entity =>
                    entity.ActorId == actor.ActorId &&
                    entity.OrganizationId == resource.OrganizationId &&
                    entity.MembershipId == membership.MembershipId)
                .OrderBy(entity => entity.GrantGroupId)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            var groupIds = grantEntities.Select(entity => entity.GrantGroupId).ToArray();
            var regions = groupIds.Length == 0
                ? Array.Empty<GrantRegionRow>()
                : await _dbContext.AccessGrantRegions
                    .AsNoTracking()
                    .Where(entity => groupIds.Contains(entity.GrantGroupId))
                    .OrderBy(entity => entity.GrantGroupId)
                    .ThenBy(entity => entity.RegionId)
                    .Select(entity => new GrantRegionRow(entity.GrantGroupId, entity.RegionId))
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            var operations = groupIds.Length == 0
                ? Array.Empty<GrantOperationRow>()
                : await _dbContext.AccessGrantOperations
                    .AsNoTracking()
                    .Where(entity => groupIds.Contains(entity.GrantGroupId))
                    .OrderBy(entity => entity.GrantGroupId)
                    .ThenBy(entity => entity.Operation)
                    .Select(entity => new GrantOperationRow(entity.GrantGroupId, entity.Operation))
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            var activeShare = await _dbContext.CaseShares
                .AsNoTracking()
                .SingleOrDefaultAsync(entity =>
                    entity.CaseId == lease.CaseId && entity.ActorId == actor.ActorId,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var grant in grantEntities)
            {
                if (grant.Status != PersistentGrantStatus.Active)
                {
                    continue;
                }

                var scopeMatchesCase = MatchesNonGeographicScope(grant, resource);
                var assignedCaseIds = scopeMatchesCase &&
                    string.Equals(resource.AccountableOwnerActorId, actor.ActorId, StringComparison.Ordinal)
                    ? new[] { lease.CaseId }
                    : Array.Empty<Guid>();
                var shareIsActive = scopeMatchesCase &&
                    activeShare is { IsActive: true } &&
                    activeShare.GrantedAtUtc <= now &&
                    (activeShare.ExpiresAtUtc is null || activeShare.ExpiresAtUtc > now);
                var sharedCaseIds = shareIsActive ? new[] { lease.CaseId } : Array.Empty<Guid>();
                var expiresAtUtc = Minimum(grant.ExpiresAtUtc, topExpiry);
                if (shareIsActive && activeShare!.ExpiresAtUtc is { } shareExpiry)
                {
                    expiresAtUtc = Minimum(expiresAtUtc, shareExpiry);
                }

                if (expiresAtUtc <= grant.ValidFromUtc)
                {
                    continue;
                }

                groups.Add(new ScopedAccessGrant(
                    grant.GrantGroupId,
                    grant.ValidFromUtc.ToUniversalTime(),
                    expiresAtUtc,
                    assignedCaseIds,
                    sharedCaseIds,
                    grant.CanAccessJurisdictionalCases,
                    [grant.OrganizationId],
                    [grant.WorkspaceId],
                    [grant.CountryCode],
                    regions.Where(region => string.Equals(region.GrantGroupId, grant.GrantGroupId, StringComparison.Ordinal))
                        .Select(region => region.RegionId),
                    [grant.Sector],
                    [grant.SensitivityCode],
                    operations.Where(operation => string.Equals(operation.GrantGroupId, grant.GrantGroupId, StringComparison.Ordinal))
                        .Select(operation => operation.Operation),
                    shareIsActive && activeShare!.AllowedOperations is { } shareOperations
                        ? new Dictionary<Guid, IEnumerable<CaseOperation>>
                        {
                            [lease.CaseId] = shareOperations.Select(value =>
                                Enum.TryParse<CaseOperation>(value, out var operation) && operation.ToString() == value && Enum.IsDefined(operation)
                                    ? operation : throw new InvalidDataException("The shared operation scope is invalid."))
                        }
                        : null));
            }
        }

        return new CurrentAccessGrant(actor.ActorId, accessState.AccessEpoch, topExpiry, status, groups);
    }

    private static bool MatchesNonGeographicScope(
        AccessGrantGroupEntity grant,
        CaseAuthorityScope resource)
    {
        return string.Equals(grant.OrganizationId, resource.OrganizationId, StringComparison.Ordinal) &&
            string.Equals(grant.WorkspaceId, resource.WorkspaceId, StringComparison.Ordinal) &&
            string.Equals(grant.CountryCode, resource.CountryCode, StringComparison.OrdinalIgnoreCase) &&
            grant.Sector == resource.Sector &&
            string.Equals(grant.SensitivityCode, resource.SensitivityCode, StringComparison.Ordinal);
    }

    private static AccessGrantStatus DetermineOverallStatus(
        PersistentActorStatus actorStatus,
        PersistentResourceStatus? organizationStatus,
        PersistentResourceStatus? workspaceStatus,
        PersistentMembershipStatus? membershipStatus)
    {
        if (actorStatus == PersistentActorStatus.Revoked ||
            organizationStatus == PersistentResourceStatus.Revoked ||
            workspaceStatus == PersistentResourceStatus.Revoked ||
            membershipStatus == PersistentMembershipStatus.Revoked)
        {
            return AccessGrantStatus.Revoked;
        }

        return actorStatus == PersistentActorStatus.Active &&
            organizationStatus == PersistentResourceStatus.Active &&
            workspaceStatus == PersistentResourceStatus.Active &&
            membershipStatus == PersistentMembershipStatus.Active
                ? AccessGrantStatus.Active
                : AccessGrantStatus.Suspended;
    }

    private static DateTimeOffset Minimum(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left.ToUniversalTime() : right.ToUniversalTime();

    private sealed record CaseAuthorityScope(
        Guid CaseId,
        string OrganizationId,
        string WorkspaceId,
        string CountryCode,
        Sector Sector,
        string SensitivityCode,
        string AccountableOwnerActorId);

    private sealed record GrantRegionRow(string GrantGroupId, string RegionId);

    private sealed record GrantOperationRow(string GrantGroupId, CaseOperation Operation);
}
