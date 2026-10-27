using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Registration;

namespace SwiftAid.Infrastructure.Authorization;

/// <summary>
/// Authorizes managed-case registration against live grant groups. Creation requires a grant
/// group carrying the <c>ReadCase</c> operation in the request's workspace, catchment country,
/// and occurrence sector; the <c>AcceptReport</c> domain capability is constructed by the
/// processor and enforced by the aggregate, mirroring the command-side split between grant
/// operations (policy) and capabilities (constructed). Replay additionally requires the
/// leased case's organization, workspace, country, sector, and sensitivity to sit inside the
/// same grant group. Sensitivity is not scoped at creation because the report carries no
/// sensitivity claim; the staging slice assigns the new case's sensitivity from the report.
/// </summary>
public sealed class PostgresCaseRegistrationAuthorization : ICaseRegistrationAuthorization
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public PostgresCaseRegistrationAuthorization(SwiftAidDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CaseRegistrationAuthorizationDecision> AuthorizeNewScopeAsync(
        ActorContext actor,
        ICaseRegistrationLease lease,
        RegisterManagedCaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        var current = RequireCreateLease(lease, actor);

        var scope = NewRegistrationScope.FromRequest(request);
        if (scope is null)
        {
            return Denied(AccessDecisionCode.InvalidResourceId);
        }

        if (!string.Equals(current.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal))
        {
            return Denied(AccessDecisionCode.WorkspaceOutsideGrant);
        }

        var actorCheck = await CheckActorAsync(actor, cancellationToken).ConfigureAwait(false);
        if (actorCheck is not null)
        {
            return actorCheck;
        }

        var workspace = await _dbContext.Workspaces.AsNoTracking()
            .Where(item => item.WorkspaceId == request.WorkspaceId)
            .Select(item => new { item.OrganizationId, item.Status })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (workspace is null)
        {
            return Denied(AccessDecisionCode.ResourceMissing);
        }

        if (workspace.Status != PersistentResourceStatus.Active)
        {
            return Denied(AccessDecisionCode.ResourceSourceUnavailable);
        }

        var membership = await ActiveMembershipAsync(actor.ActorId, workspace.OrganizationId, cancellationToken)
            .ConfigureAwait(false);
        if (membership is null)
        {
            return await MissingMembershipCodeAsync(actor.ActorId, workspace.OrganizationId, cancellationToken)
                .ConfigureAwait(false);
        }

        var match = await FindGrantGroupAsync(
            actor.ActorId, membership.MembershipId, workspace.OrganizationId, request.WorkspaceId,
            scope.CountryCode, scope.Sector, null, cancellationToken).ConfigureAwait(false);
        if (match is not null)
        {
            return Allowed(match);
        }

        return await MissedGrantCodeAsync(
            actor.ActorId, membership.MembershipId, workspace.OrganizationId, request.WorkspaceId,
            scope.CountryCode, scope.Sector, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CaseRegistrationAuthorizationDecision> AuthorizeExistingCaseReplayAsync(
        ActorContext actor,
        ICaseRegistrationLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var current = RequireReplayLease(lease, actor);

        var actorCheck = await CheckActorAsync(actor, cancellationToken).ConfigureAwait(false);
        if (actorCheck is not null)
        {
            return actorCheck;
        }

        var scope = await _dbContext.ManagedCases.AsNoTracking()
            .Where(item => item.CaseId == current.CaseId)
            .Select(item => new
            {
                item.OrganizationId,
                item.WorkspaceId,
                item.CountryCode,
                Sector = item.Sector.ToString(),
                item.SensitivityCode
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (scope is null)
        {
            return Denied(AccessDecisionCode.ResourceMissing);
        }

        if (!Enum.TryParse<Sector>(scope.Sector, out var sector))
        {
            return Denied(AccessDecisionCode.ResourceSourceUnavailable);
        }

        var membership = await ActiveMembershipAsync(actor.ActorId, scope.OrganizationId, cancellationToken)
            .ConfigureAwait(false);
        if (membership is null)
        {
            return await MissingMembershipCodeAsync(actor.ActorId, scope.OrganizationId, cancellationToken)
                .ConfigureAwait(false);
        }

        var match = await FindGrantGroupAsync(
            actor.ActorId, membership.MembershipId, scope.OrganizationId, scope.WorkspaceId,
            scope.CountryCode, sector, scope.SensitivityCode, cancellationToken).ConfigureAwait(false);
        if (match is not null)
        {
            return Allowed(match);
        }

        return await MissedGrantCodeAsync(
            actor.ActorId, membership.MembershipId, scope.OrganizationId, scope.WorkspaceId,
            scope.CountryCode, sector, scope.SensitivityCode, cancellationToken).ConfigureAwait(false);
    }

    private PostgresCaseRegistrationLease RequireLease(
        ICaseRegistrationLease lease, ActorContext actor, CaseRegistrationLeaseMode mode)
    {
        if (lease is not PostgresCaseRegistrationLease current || !current.IsActive)
        {
            throw new ArgumentException("An active registration lease is required.", nameof(lease));
        }

        if (current.Mode != mode)
        {
            throw new ArgumentException("The registration lease mode does not support this operation.", nameof(lease));
        }

        if (!string.Equals(current.PrimaryActorId, actor.ActorId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The registration lease belongs to a different actor.", nameof(actor));
        }

        return current;
    }

    private Persistence.Registration.PostgresCaseRegistrationLease RequireCreateLease(
        ICaseRegistrationLease lease, ActorContext actor) =>
        RequireLease(lease, actor, CaseRegistrationLeaseMode.Create);

    private Persistence.Registration.PostgresCaseRegistrationLease RequireReplayLease(
        ICaseRegistrationLease lease, ActorContext actor) =>
        RequireLease(lease, actor, CaseRegistrationLeaseMode.ExistingCaseReplay);

    private async Task<CaseRegistrationAuthorizationDecision?> CheckActorAsync(
        ActorContext actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor.ActorId))
        {
            return Denied(AccessDecisionCode.GrantActorMismatch);
        }

        var state = await _dbContext.Actors.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId)
            .Select(item => new { item.Status, item.ExpiresAtUtc })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        if (state is null || state.Status != PersistentActorStatus.Active ||
            (state.ExpiresAtUtc is { } expiry && expiry <= now))
        {
            return Denied(AccessDecisionCode.GrantActorMismatch);
        }

        var epoch = await _dbContext.ActorAccessStates.AsNoTracking()
            .Where(item => item.ActorId == actor.ActorId)
            .Select(item => (long?)item.AccessEpoch)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (epoch != actor.AccessEpoch)
        {
            return Denied(AccessDecisionCode.AccessEpochMismatch);
        }

        return null;
    }

    private async Task<MembershipRef?> ActiveMembershipAsync(
        string actorId, string organizationId, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var membership = await _dbContext.OrganizationMemberships.AsNoTracking()
            .Where(item => item.ActorId == actorId && item.OrganizationId == organizationId)
            .OrderByDescending(item => item.ValidFromUtc)
            .Select(item => new { item.MembershipId, item.Status, item.ValidFromUtc, item.ExpiresAtUtc })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (membership is null || membership.Status != PersistentMembershipStatus.Active ||
            membership.ValidFromUtc > now ||
            (membership.ExpiresAtUtc is { } expiry && expiry <= now))
        {
            return null;
        }

        return new MembershipRef(membership.MembershipId);
    }

    private async Task<CaseRegistrationAuthorizationDecision> MissingMembershipCodeAsync(
        string actorId, string organizationId, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var expired = await _dbContext.OrganizationMemberships.AsNoTracking()
            .AnyAsync(item => item.ActorId == actorId && item.OrganizationId == organizationId &&
                item.Status == PersistentMembershipStatus.Active &&
                item.ExpiresAtUtc != null && item.ExpiresAtUtc <= now, ct).ConfigureAwait(false);
        return Denied(expired ? AccessDecisionCode.GrantExpired : AccessDecisionCode.GrantMissing);
    }

    private async Task<string?> FindGrantGroupAsync(
        string actorId, Guid membershipId, string organizationId, string workspaceId,
        string countryCode, Sector sector, string? sensitivityCode, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var candidates =
            from item in _dbContext.AccessGrantGroups.AsNoTracking()
            join organization in _dbContext.Organizations.AsNoTracking()
                on item.OrganizationId equals organization.OrganizationId
            join workspace in _dbContext.Workspaces.AsNoTracking()
                on item.WorkspaceId equals workspace.WorkspaceId
            where item.ActorId == actorId &&
                item.OrganizationId == organizationId &&
                item.MembershipId == membershipId &&
                item.WorkspaceId == workspaceId &&
                item.Status == PersistentGrantStatus.Active &&
                item.ValidFromUtc <= now && item.ExpiresAtUtc > now &&
                item.CountryCode == countryCode &&
                item.Sector == sector &&
                (sensitivityCode == null || item.SensitivityCode == sensitivityCode) &&
                organization.Status == PersistentResourceStatus.Active &&
                workspace.Status == PersistentResourceStatus.Active &&
                _dbContext.AccessGrantOperations.AsNoTracking().Any(operation =>
                    operation.GrantGroupId == item.GrantGroupId &&
                    operation.Operation == CaseOperation.ReadCase)
            orderby item.GrantGroupId
            select item.GrantGroupId;
        return await candidates.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private async Task<CaseRegistrationAuthorizationDecision> MissedGrantCodeAsync(
        string actorId, Guid membershipId, string organizationId, string workspaceId,
        string countryCode, Sector sector, string? sensitivityCode, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var grants = await _dbContext.AccessGrantGroups.AsNoTracking()
            .Where(item => item.ActorId == actorId &&
                item.MembershipId == membershipId &&
                item.Status == PersistentGrantStatus.Active)
            .Select(item => new
            {
                item.OrganizationId,
                item.WorkspaceId,
                item.CountryCode,
                item.Sector,
                item.SensitivityCode,
                item.ValidFromUtc,
                item.ExpiresAtUtc,
                HasReadCase = _dbContext.AccessGrantOperations.AsNoTracking().Any(operation =>
                    operation.GrantGroupId == item.GrantGroupId &&
                    operation.Operation == CaseOperation.ReadCase)
            })
            .ToArrayAsync(ct).ConfigureAwait(false);
        if (grants.Length == 0)
        {
            var expired = await _dbContext.AccessGrantGroups.AsNoTracking()
                .AnyAsync(item => item.ActorId == actorId && item.MembershipId == membershipId &&
                    item.Status == PersistentGrantStatus.Active && item.ExpiresAtUtc <= now, ct)
                .ConfigureAwait(false);
            return Denied(expired ? AccessDecisionCode.GrantExpired : AccessDecisionCode.GrantMissing);
        }

        if (!grants.Any(item => item.HasReadCase))
        {
            return Denied(AccessDecisionCode.OperationNotGranted);
        }

        if (!grants.Any(item => string.Equals(item.OrganizationId, organizationId, StringComparison.Ordinal)))
        {
            return Denied(AccessDecisionCode.OrganizationOutsideGrant);
        }

        if (!grants.Any(item => string.Equals(item.WorkspaceId, workspaceId, StringComparison.Ordinal)))
        {
            return Denied(AccessDecisionCode.WorkspaceOutsideGrant);
        }

        if (!grants.Any(item => string.Equals(item.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase)))
        {
            return Denied(AccessDecisionCode.CountryOutsideGrant);
        }

        if (!grants.Any(item => item.Sector == sector))
        {
            return Denied(AccessDecisionCode.SectorOutsideGrant);
        }

        if (sensitivityCode is not null && !grants.Any(item =>
                string.Equals(item.SensitivityCode, sensitivityCode, StringComparison.Ordinal)))
        {
            return Denied(AccessDecisionCode.SensitivityOutsideGrant);
        }

        return Denied(AccessDecisionCode.GrantExpired);
    }

    private static CaseRegistrationAuthorizationDecision Allowed(string grantGroupId) =>
        new(true, AccessDecisionCode.Allowed, grantGroupId);

    private static CaseRegistrationAuthorizationDecision Denied(AccessDecisionCode code) =>
        new(false, code, null);

    private sealed record MembershipRef(Guid MembershipId);

    private sealed record NewRegistrationScope(string CountryCode, Sector Sector)
    {
        public static NewRegistrationScope? FromRequest(RegisterManagedCaseRequest request)
        {
            var country = request.Report switch
            {
                CaseReportEventData.HumanV2 human => human.Confirmations.OccurrenceCountryCode,
                CaseReportEventData.AnimalV2 animal => animal.Confirmations.OccurrenceCountryCode,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(country))
            {
                return null;
            }

            var sector = request.Report switch
            {
                CaseReportEventData.HumanV2 => Sector.HumanHealth,
                CaseReportEventData.AnimalV2 => Sector.AnimalHealth,
                _ => (Sector?)null
            };
            if (sector is null)
            {
                return null;
            }

            return new NewRegistrationScope(country, sector.Value);
        }
    }
}
