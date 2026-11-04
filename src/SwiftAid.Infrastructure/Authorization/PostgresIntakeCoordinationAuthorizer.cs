using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Infrastructure.Intake;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Authorization;

/// <summary>
/// Authorizes PC-09 platform coordinators against an explicit grant in the configured
/// coordination organization. The actor needs an active membership, a current access
/// epoch, and at least one active in-window grant group carrying
/// <c>CoordinateIntake</c>. Report-scoped checks additionally require the receipt to be
/// an unrouted, non-terminal report; routed or terminal reports are never
/// coordinator-visible, so coordinators cannot reach organization casework.
/// </summary>
public sealed class PostgresIntakeCoordinationAuthorizer(
    SwiftAidDbContext dbContext,
    IntakeCoordinationOptions coordinationOptions,
    TimeProvider? timeProvider = null) : IIntakeCoordinationAuthorizer
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IntakeCoordinationOptions _options =
        coordinationOptions ?? throw new ArgumentNullException(nameof(coordinationOptions));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<IntakeCoordinationAuthority?> AuthorizeAsync(ActorContext actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return AuthorizeCoreAsync(actor, null, ct);
    }

    public Task<IntakeCoordinationAuthority?> AuthorizeReportAsync(
        ActorContext actor,
        Guid receiptId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (receiptId == Guid.Empty)
        {
            return Task.FromResult<IntakeCoordinationAuthority?>(null);
        }

        return AuthorizeCoreAsync(actor, receiptId, ct);
    }

    private async Task<IntakeCoordinationAuthority?> AuthorizeCoreAsync(
        ActorContext actor,
        Guid? receiptId,
        CancellationToken ct)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(actor.ActorId) ||
            string.IsNullOrWhiteSpace(_options.CoordinationOrganizationId))
        {
            return null;
        }

        if (receiptId is { } scopedReceipt)
        {
            var visible = await IsUnroutedNonTerminalAsync(scopedReceipt, ct).ConfigureAwait(false);
            if (!visible)
            {
                return null;
            }
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var actorEntity = await _dbContext.Actors.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId)
            .Select(item => new { item.Status, item.ExpiresAtUtc })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (actorEntity is null || actorEntity.Status != PersistentActorStatus.Active ||
            (actorEntity.ExpiresAtUtc is { } actorExpiry && actorExpiry <= now))
        {
            return null;
        }

        var accessEpoch = await _dbContext.ActorAccessStates.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId)
            .Select(item => (long?)item.AccessEpoch)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (accessEpoch != actor.AccessEpoch)
        {
            return null;
        }

        var organizationActive = await _dbContext.Organizations.AsNoTracking()
            .Where(item => item.OrganizationId == _options.CoordinationOrganizationId)
            .Select(item => item.Status == PersistentResourceStatus.Active)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (!organizationActive)
        {
            return null;
        }

        var membership = await _dbContext.OrganizationMemberships.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId &&
                item.OrganizationId == _options.CoordinationOrganizationId)
            .Select(item => new { item.MembershipId, item.Status, item.ValidFromUtc, item.ExpiresAtUtc })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (membership is null || membership.Status != PersistentMembershipStatus.Active ||
            membership.ValidFromUtc > now ||
            (membership.ExpiresAtUtc is { } membershipExpiry && membershipExpiry <= now))
        {
            return null;
        }

        var grantGroupId = await _dbContext.AccessGrantGroups.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId &&
                item.OrganizationId == _options.CoordinationOrganizationId &&
                item.MembershipId == membership.MembershipId &&
                item.Status == PersistentGrantStatus.Active &&
                item.ValidFromUtc <= now && item.ExpiresAtUtc > now)
            .OrderBy(item => item.GrantGroupId)
            .Select(item => item.GrantGroupId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(grantGroupId))
        {
            return null;
        }

        var coordinated = await _dbContext.AccessGrantOperations.AsNoTracking()
            .AnyAsync(item => item.GrantGroupId == grantGroupId &&
                item.Operation == CaseOperation.CoordinateIntake, ct).ConfigureAwait(false);
        if (!coordinated)
        {
            return null;
        }

        return new IntakeCoordinationAuthority(
            actor.ActorId, _options.CoordinationOrganizationId, grantGroupId);
    }

    private async Task<bool> IsUnroutedNonTerminalAsync(Guid receiptId, CancellationToken ct)
    {
        var report = await _dbContext.IntakeReports.AsNoTracking()
            .Where(item => item.ReceiptId == receiptId)
            .Select(item => new { item.Status, item.RoutingStatus, item.IntakeQueueId })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (report is null)
        {
            return false;
        }

        if (report.Status is IntakeStatus.AcceptedIntoCase or IntakeStatus.LinkedAsEvidence or IntakeStatus.Declined)
        {
            return false;
        }

        return report.IntakeQueueId is null &&
            report.RoutingStatus is IntakeRoutingStatus.NeedsRouting or IntakeRoutingStatus.NoServiceCoverage;
    }
}
