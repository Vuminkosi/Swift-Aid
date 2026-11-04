using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Sync;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Authorization;

/// <summary>Reads session-wide authority without requiring a case-command lease.</summary>
public sealed class PostgresSessionScopeService(SwiftAidDbContext db, TimeProvider clock) : IScopeService
{
    public async ValueTask<ScopeManifest> GetAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        // Keep identity, grants and shares in one database snapshot.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken)
            : null;
        var now = clock.GetUtcNow();
        var identity = await (from account in db.Actors.AsNoTracking()
                              join state in db.ActorAccessStates.AsNoTracking() on account.ActorId equals state.ActorId
                              where account.ActorId == actor.ActorId
                              select new { account.Status, account.ExpiresAtUtc, state.AccessEpoch })
            .SingleOrDefaultAsync(cancellationToken);
        if (identity is null || identity.Status != PersistentActorStatus.Active || identity.ExpiresAtUtc <= now)
            throw new ScopeUnavailableException(ScopeFailureCode.GrantMissing);
        if (identity.AccessEpoch != actor.AccessEpoch)
            throw new ScopeUnavailableException(ScopeFailureCode.AccessEpochMismatch);

        var groups = await (from grant in db.AccessGrantGroups.AsNoTracking()
                            join membership in db.OrganizationMemberships on grant.MembershipId equals membership.MembershipId
                            join organization in db.Organizations on grant.OrganizationId equals organization.OrganizationId
                            join workspace in db.Workspaces on grant.WorkspaceId equals workspace.WorkspaceId
                            where grant.ActorId == actor.ActorId && membership.ActorId == actor.ActorId
                                && membership.OrganizationId == grant.OrganizationId
                                && grant.Status == PersistentGrantStatus.Active
                                && grant.ValidFromUtc <= now && grant.ExpiresAtUtc > now
                                && membership.Status == PersistentMembershipStatus.Active
                                && membership.ValidFromUtc <= now && (membership.ExpiresAtUtc == null || membership.ExpiresAtUtc > now)
                                && organization.Status == PersistentResourceStatus.Active
                                && workspace.Status == PersistentResourceStatus.Active
                                && db.WorkspaceOrganizations.Any(link => link.OrganizationId == grant.OrganizationId
                                    && link.WorkspaceId == grant.WorkspaceId && link.Status == PersistentResourceStatus.Active)
                            select new { Grant = grant, MembershipExpiry = membership.ExpiresAtUtc })
            .ToArrayAsync(cancellationToken);
        if (groups.Length == 0) throw new ScopeUnavailableException(ScopeFailureCode.GrantMissing);

        var ids = groups.Select(group => group.Grant.GrantGroupId).ToArray();
        var regions = await db.AccessGrantRegions.AsNoTracking().Where(row => ids.Contains(row.GrantGroupId)).ToArrayAsync(cancellationToken);
        var operations = await db.AccessGrantOperations.AsNoTracking().Where(row => ids.Contains(row.GrantGroupId)).ToArrayAsync(cancellationToken);
        var assigned = await db.ManagedCases.AsNoTracking().Where(row => row.AccountableOwnerActorId == actor.ActorId)
            .Select(row => new { row.CaseId, row.OrganizationId, row.WorkspaceId, row.CountryCode, row.Sector, row.SensitivityCode })
            .ToArrayAsync(cancellationToken);
        var shared = await (from share in db.CaseShares.AsNoTracking()
                            join resource in db.ManagedCases on share.CaseId equals resource.CaseId
                            where share.ActorId == actor.ActorId && share.IsActive && share.GrantedAtUtc <= now
                                && (share.ExpiresAtUtc == null || share.ExpiresAtUtc > now)
                            select new { Share = share, Resource = resource }).ToArrayAsync(cancellationToken);
        var manifests = groups.SelectMany(item =>
        {
            var grant = item.Grant;
            var expiry = new[] { grant.ExpiresAtUtc, item.MembershipExpiry ?? DateTimeOffset.MaxValue,
                identity.ExpiresAtUtc ?? DateTimeOffset.MaxValue }.Min();
            var entries = new List<ScopeGrantManifest>();
            entries.Add(new ScopeGrantManifest(grant.GrantGroupId, grant.ValidFromUtc, expiry,
                [grant.OrganizationId], [grant.WorkspaceId],
                assigned.Where(row => row.OrganizationId == grant.OrganizationId && row.WorkspaceId == grant.WorkspaceId
                    && row.CountryCode == grant.CountryCode && row.Sector == grant.Sector && row.SensitivityCode == grant.SensitivityCode)
                    .Select(row => row.CaseId),
                [grant.CountryCode], regions.Where(row => row.GrantGroupId == grant.GrantGroupId).Select(row => row.RegionId),
                [grant.SensitivityCode], [grant.Sector.ToString()],
                operations.Where(row => row.GrantGroupId == grant.GrantGroupId).Select(row => row.Operation.ToString()),
                grant.CanAccessJurisdictionalCases));
            foreach (var itemShare in shared.Where(row => row.Resource.OrganizationId == grant.OrganizationId
                && row.Resource.WorkspaceId == grant.WorkspaceId
                && string.Equals(row.Resource.CountryCode, grant.CountryCode, StringComparison.OrdinalIgnoreCase)
                && row.Resource.Sector == grant.Sector && row.Resource.SensitivityCode == grant.SensitivityCode))
            {
                var share = itemShare.Share;
                var shareOperations = operations.Where(row => row.GrantGroupId == grant.GrantGroupId)
                    .Select(row => row.Operation.ToString());
                if (share.AllowedOperations is not null)
                    shareOperations = shareOperations.Intersect(share.AllowedOperations, StringComparer.Ordinal);
                var shareExpiry = share.ExpiresAtUtc is { } end && end < expiry ? end : expiry;
                entries.Add(new ScopeGrantManifest($"{grant.GrantGroupId}:share:{share.CaseId:D}",
                    grant.ValidFromUtc, shareExpiry, [grant.OrganizationId], [grant.WorkspaceId], [share.CaseId],
                    [grant.CountryCode], regions.Where(row => row.GrantGroupId == grant.GrantGroupId).Select(row => row.RegionId),
                    [grant.SensitivityCode], [grant.Sector.ToString()], shareOperations, false));
            }
            return entries;
        }).ToArray();
        var manifest = new ScopeManifest(manifests, actor.AccessEpoch, manifests.Min(group => group.ExpiresAtUtc));
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return manifest;
    }
}
