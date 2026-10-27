using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Authorization;

/// <summary>
/// Authorizes reviewer decisions against the receiving organization's grant groups. The actor
/// needs an active membership, a current access epoch, and at least one active in-window
/// grant group carrying the <c>ReviewIntake</c> operation in the route's catchment country.
/// Unrouted reports (no queue) have no authorized reviewer. Evidence linkage additionally
/// requires same-organization containment between the report's queue and the target case.
/// Sector and sensitivity narrowing beyond organization and country remain a follow-up; the
/// enforced scope is documented here rather than silently broadened.
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
        var scope = await ResolveScopeAsync(actor, receiptId, ct).ConfigureAwait(false);
        if (scope is null)
        {
            return false;
        }

        return await HasReviewGrantAsync(actor, scope, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsLinkAuthorizedAsync(
        ActorContext actor,
        Guid receiptId,
        Guid targetCaseId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (targetCaseId == Guid.Empty)
        {
            return false;
        }

        var scope = await ResolveScopeAsync(actor, receiptId, ct).ConfigureAwait(false);
        if (scope is null)
        {
            return false;
        }

        var caseOrganizationId = await _dbContext.ManagedCases.AsNoTracking()
            .Where(item => item.CaseId == targetCaseId)
            .Select(item => item.OrganizationId)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(caseOrganizationId) ||
            !string.Equals(caseOrganizationId, scope.OrganizationId, StringComparison.Ordinal))
        {
            return false;
        }

        return await HasReviewGrantAsync(actor, scope, ct).ConfigureAwait(false);
    }

    private async Task<ReviewScope?> ResolveScopeAsync(ActorContext actor, Guid receiptId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor.ActorId) || receiptId == Guid.Empty)
        {
            return null;
        }

        var queueId = await _dbContext.IntakeReports.AsNoTracking()
            .Where(item => item.ReceiptId == receiptId)
            .Select(item => item.IntakeQueueId)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(queueId))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var route = await _dbContext.IntakeRoutes.AsNoTracking()
            .Where(item => item.IntakeQueueId == queueId &&
                item.IsActive &&
                item.EffectiveFromUtc <= now &&
                (item.ExpiresAtUtc == null || item.ExpiresAtUtc > now))
            .OrderByDescending(item => item.EffectiveFromUtc)
            .Select(item => new { item.ReceivingOrganizationId, item.CountryCode })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (route is null || string.IsNullOrWhiteSpace(route.ReceivingOrganizationId))
        {
            return null;
        }

        return new ReviewScope(route.ReceivingOrganizationId, route.CountryCode, now);
    }

    private async Task<bool> HasReviewGrantAsync(ActorContext actor, ReviewScope scope, CancellationToken ct)
    {
        var actorEntity = await _dbContext.Actors.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId)
            .Select(item => new { item.Status, item.ExpiresAtUtc })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (actorEntity is null || actorEntity.Status != PersistentActorStatus.Active ||
            (actorEntity.ExpiresAtUtc is { } actorExpiry && actorExpiry <= scope.Now))
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
            .Where(item => item.OrganizationId == scope.OrganizationId)
            .Select(item => item.Status == PersistentResourceStatus.Active)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (!organizationActive)
        {
            return false;
        }

        var membership = await _dbContext.OrganizationMemberships.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId &&
                item.OrganizationId == scope.OrganizationId)
            .Select(item => new { item.MembershipId, item.Status, item.ValidFromUtc, item.ExpiresAtUtc })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (membership is null || membership.Status != PersistentMembershipStatus.Active ||
            membership.ValidFromUtc > scope.Now ||
            (membership.ExpiresAtUtc is { } membershipExpiry && membershipExpiry <= scope.Now))
        {
            return false;
        }

        var grants = await _dbContext.AccessGrantGroups.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId &&
                item.OrganizationId == scope.OrganizationId &&
                item.MembershipId == membership.MembershipId &&
                item.Status == PersistentGrantStatus.Active &&
                item.ValidFromUtc <= scope.Now && item.ExpiresAtUtc > scope.Now)
            .Select(item => new { item.GrantGroupId, item.CountryCode })
            .ToArrayAsync(ct).ConfigureAwait(false);
        var countryGrants = grants
            .Where(item => string.Equals(item.CountryCode, scope.CountryCode, StringComparison.OrdinalIgnoreCase))
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

    private sealed record ReviewScope(string OrganizationId, string? CountryCode, DateTimeOffset Now);
}

