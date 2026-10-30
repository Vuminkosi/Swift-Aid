using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Reads active case shares, membership organizations, and display names for
/// the home overview. No sealed payloads, narratives, or coordinates are read.
/// </summary>
public sealed class PostgresHomeOverviewStore(SwiftAidDbContext dbContext) : IHomeOverviewStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<HomeOverviewShare>> GetActiveSharesAsync(string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var now = DateTimeOffset.UtcNow;
        return await _dbContext.CaseShares.AsNoTracking()
            .Where(share => share.ActorId == actorId &&
                share.IsActive &&
                (share.ExpiresAtUtc == null || share.ExpiresAtUtc > now))
            .OrderByDescending(share => share.GrantedAtUtc)
            .Select(share => new HomeOverviewShare(share.CaseId, share.GrantedAtUtc))
            .Take(50)
            .ToArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetMembershipOrganizationIdsAsync(string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return await _dbContext.OrganizationMemberships.AsNoTracking()
            .Where(membership => membership.ActorId == actorId &&
                membership.Status == PersistentMembershipStatus.Active)
            .Select(membership => membership.OrganizationId)
            .Distinct()
            .OrderBy(organizationId => organizationId)
            .ToArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> GetDisplayNameAsync(string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return await _dbContext.UserProfiles.AsNoTracking()
            .Where(profile => profile.UserId == actorId)
            .Select(profile => profile.DisplayName)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }
}
