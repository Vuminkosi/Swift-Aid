using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Authorization;

/// <summary>
/// Authorizes reviewer decisions against the receiving organization's grant groups. The actor
/// needs an active membership, a current access epoch, and at least one active in-window
/// grant group carrying the <c>ReviewIntake</c> operation in the route's catchment country.
/// Unrouted reports (no queue) have no authorized reviewer. Sector and sensitivity narrowing
/// beyond organization and country remain a follow-up; the enforced scope is documented here
/// rather than silently broadened.
/// </summary>
public sealed class PostgresIntakeReviewAuthorizer : IIntakeReviewAuthorizer
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public PostgresIntakeReviewAuthorizer(SwiftAidDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> IsAuthorizedAsync(ActorContext actor, Guid receiptId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.ActorId) || receiptId == Guid.Empty)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();

        var report = await _dbContext.IntakeReports.AsNoTracking()
            .Where(item => item.ReceiptId == receiptId)
            .Select(item => new { item.IntakeQueueId })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (report is null || string.IsNullOrWhiteSpace(report.IntakeQueueId))
        {
            return false;
        }

        var route = await _dbContext.IntakeRoutes.AsNoTracking()
            .Where(item => item.IntakeQueueId == report.IntakeQueueId &&
                item.IsActive &&
                item.EffectiveFromUtc <= now &&
                (item.ExpiresAtUtc == null || item.ExpiresAtUtc > now))
            .OrderByDescending(item => item.EffectiveFromUtc)
            .Select(item => new { item.ReceivingOrganizationId, item.CountryCode })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (route is null || string.IsNullOrWhiteSpace(route.ReceivingOrganizationId))
        {
            return false;
        }

        var actorEntity = await _dbContext.Actors.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId)
            .Select(item => new { item.Status, item.ExpiresAtUtc })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (actorEntity is null || actorEntity.Status != PersistentActorStatus.Active ||
            (actorEntity.ExpiresAtUtc is { } actorExpiry && actorExpiry <= now))
        {
            return false;
        }

        var accessEpoch = await _dbContext.ActorAccessStates.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId)
            .Select(item => (long?)item.AccessEpoch)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (accessEpoch != actor.AccessEpoch)
        {
            return false;
        }

        var organizationActive = await _dbContext.Organizations.AsNoTracking()
            .Where(item => item.OrganizationId == route.ReceivingOrganizationId)
            .Select(item => item.Status == PersistentResourceStatus.Active)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (!organizationActive)
        {
            return false;
        }

        var membership = await _dbContext.OrganizationMemberships.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId &&
                item.OrganizationId == route.ReceivingOrganizationId)
            .Select(item => new { item.MembershipId, item.Status, item.ValidFromUtc, item.ExpiresAtUtc })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (membership is null || membership.Status != PersistentMembershipStatus.Active ||
            membership.ValidFromUtc > now ||
            (membership.ExpiresAtUtc is { } membershipExpiry && membershipExpiry <= now))
        {
            return false;
        }

        var grants = await _dbContext.AccessGrantGroups.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId &&
                item.OrganizationId == route.ReceivingOrganizationId &&
                item.MembershipId == membership.MembershipId &&
                item.Status == PersistentGrantStatus.Active &&
                item.ValidFromUtc <= now && item.ExpiresAtUtc > now)
            .Select(item => new { item.GrantGroupId, item.CountryCode })
            .ToArrayAsync(ct).ConfigureAwait(false);
        var countryGrants = grants
            .Where(item => string.Equals(item.CountryCode, route.CountryCode, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.GrantGroupId)
            .ToArray();
        if (countryGrants.Length == 0)
        {
            return false;
        }

        return await _dbContext.AccessGrantOperations.AsNoTracking()
            .AnyAsync(item => countryGrants.Contains(item.GrantGroupId) &&
                item.Operation == CaseOperation.ReviewIntake, ct).ConfigureAwait(false);
    }
}
