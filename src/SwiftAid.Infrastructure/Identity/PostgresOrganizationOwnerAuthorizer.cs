using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Identity;

/// <summary>Resolves owner authority from active persisted assignments and membership.</summary>
public sealed class PostgresOrganizationOwnerAuthorizer(
    SwiftAidDbContext dbContext,
    TimeProvider timeProvider) : IOrganizationOwnerAuthorizer
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public Task<bool> IsActiveOwnerAsync(string actorId, string organizationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(organizationId))
            return Task.FromResult(false);

        var now = _timeProvider.GetUtcNow();
        return _dbContext.OrganizationOwners.AsNoTracking().AnyAsync(owner =>
            owner.ActorId == actorId && owner.OrganizationId == organizationId &&
            owner.RevokedAtUtc == null &&
            _dbContext.Organizations.Any(organization => organization.OrganizationId == organizationId &&
                organization.Status == PersistentResourceStatus.Active) &&
            _dbContext.OrganizationMemberships.Any(membership => membership.ActorId == actorId &&
                membership.OrganizationId == organizationId && membership.Status == PersistentMembershipStatus.Active &&
                membership.ValidFromUtc <= now && (membership.ExpiresAtUtc == null || membership.ExpiresAtUtc > now)),
            cancellationToken);
    }
}
