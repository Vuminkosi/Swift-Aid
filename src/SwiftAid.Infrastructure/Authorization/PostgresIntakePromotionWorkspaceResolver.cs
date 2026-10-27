using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Commands;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Authorization;

/// <summary>Resolves the officer's eligible registration workspaces for a promotion scope.</summary>
/// <remarks>
/// Mirrors the registration authorization predicates (<c>PostgresCaseRegistrationAuthorization</c>)
/// without a workspace preselection: active identity with a matching access epoch, an active
/// in-window membership in the receiving organization, and at least one active in-window
/// grant group carrying <c>ReadCase</c> in the report's occurrence country and sector.
/// Returns every distinct eligible workspace; the caller fails closed on zero or several.
/// </remarks>
public sealed class PostgresIntakePromotionWorkspaceResolver : IIntakePromotionWorkspaceResolver
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public PostgresIntakePromotionWorkspaceResolver(SwiftAidDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<string>> FindEligibleWorkspacesAsync(
        ActorContext actor,
        string organizationId,
        string countryCode,
        Sector sector,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.ActorId) || string.IsNullOrWhiteSpace(organizationId) ||
            string.IsNullOrWhiteSpace(countryCode) || !Enum.IsDefined(sector))
        {
            return [];
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var epoch = await _dbContext.ActorAccessStates.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId)
            .Select(item => (long?)item.AccessEpoch)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (epoch != actor.AccessEpoch)
        {
            return [];
        }

        var membership = await _dbContext.OrganizationMemberships.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId &&
                item.OrganizationId == organizationId &&
                item.Status == PersistentMembershipStatus.Active &&
                item.ValidFromUtc <= now &&
                (item.ExpiresAtUtc == null || item.ExpiresAtUtc > now))
            .Select(item => item.MembershipId)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (membership == Guid.Empty)
        {
            return [];
        }

        return await (
            from item in _dbContext.AccessGrantGroups.AsNoTracking()
            join organization in _dbContext.Organizations.AsNoTracking()
                on item.OrganizationId equals organization.OrganizationId
            join workspace in _dbContext.Workspaces.AsNoTracking()
                on item.WorkspaceId equals workspace.WorkspaceId
            where item.ActorId == actor.ActorId &&
                item.OrganizationId == organizationId &&
                item.MembershipId == membership &&
                item.Status == PersistentGrantStatus.Active &&
                item.ValidFromUtc <= now && item.ExpiresAtUtc > now &&
                item.CountryCode == countryCode &&
                item.Sector == sector &&
                organization.Status == PersistentResourceStatus.Active &&
                workspace.Status == PersistentResourceStatus.Active &&
                _dbContext.AccessGrantOperations.AsNoTracking().Any(operation =>
                    operation.GrantGroupId == item.GrantGroupId &&
                    operation.Operation == CaseOperation.ReadCase)
            orderby item.WorkspaceId
            select item.WorkspaceId)
            .Distinct()
            .ToArrayAsync(ct).ConfigureAwait(false);
    }
}
