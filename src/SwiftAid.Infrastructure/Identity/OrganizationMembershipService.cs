using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Identity;
using SwiftAid.Contracts.Commands;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Identity;

public sealed record ActorMembershipRecord(
    string OrganizationId,
    string OrganizationStatus,
    string MembershipStatus,
    IReadOnlyList<string> WorkspaceIds,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record MembershipDirectoryRecord(string OrganizationId, string Status);

public sealed record MembershipRequestRecord(
    Guid RequestId,
    string OrganizationId,
    string Status,
    string? Reason,
    DateTimeOffset CreatedAtUtc);

public enum MembershipRequestSubmitStatus
{
    Submitted,
    DuplicatePending,
    AlreadyMember,
    OrganizationUnavailable,
    Invalid,
    Unavailable
}

public sealed record MembershipRequestSubmitResult(
    MembershipRequestSubmitStatus Status,
    MembershipRequestRecord? Request = null);

public enum OrganizationCreateStatus
{
    Created,
    DuplicateId,
    Invalid,
    Unavailable
}

public sealed record OrganizationCreateResult(
    OrganizationCreateStatus Status,
    string? OrganizationId = null);

public enum OrganizationOwnerChangeStatus { Changed, Unchanged, Denied, Invalid, Unavailable }

public interface IOrganizationMembershipService
{
    Task<OrganizationCreateResult> CreateAsync(
        string actorId,
        string organizationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ActorMembershipRecord>> GetMineAsync(string actorId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MembershipDirectoryRecord>> SearchAsync(string? query, CancellationToken cancellationToken);

    Task<MembershipRequestSubmitResult> RequestAsync(
        string actorId,
        string organizationId,
        string? reason,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MembershipRequestRecord>> GetMyRequestsAsync(string actorId, CancellationToken cancellationToken);

    Task<OrganizationOwnerChangeStatus> GrantOwnerAsync(string actorId, string organizationId, string targetActorId, CancellationToken cancellationToken);

    Task<OrganizationOwnerChangeStatus> RevokeOwnerAsync(string actorId, string organizationId, string targetActorId, CancellationToken cancellationToken);
}

/// <summary>Member-side organization access: read memberships, browse the directory,
/// request to join. Review and grants stay with staff tooling and authority tables.</summary>
public sealed class OrganizationMembershipService(
    SwiftAidDbContext dbContext,
    TimeProvider timeProvider,
    IOrganizationOwnerAuthorizer ownerAuthorizer) : IOrganizationMembershipService
{
    private const int DirectoryLimit = 25;

    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IOrganizationOwnerAuthorizer _ownerAuthorizer = ownerAuthorizer ?? throw new ArgumentNullException(nameof(ownerAuthorizer));

    public Task<OrganizationOwnerChangeStatus> GrantOwnerAsync(string actorId, string organizationId, string targetActorId, CancellationToken cancellationToken) =>
        ChangeOwnerAsync(actorId, organizationId, targetActorId, revoke: false, cancellationToken);

    public Task<OrganizationOwnerChangeStatus> RevokeOwnerAsync(string actorId, string organizationId, string targetActorId, CancellationToken cancellationToken) =>
        ChangeOwnerAsync(actorId, organizationId, targetActorId, revoke: true, cancellationToken);

    private async Task<OrganizationOwnerChangeStatus> ChangeOwnerAsync(
        string actorId, string organizationId, string targetActorId, bool revoke, CancellationToken cancellationToken)
    {
        var organization = NormalizeOrganizationId(organizationId);
        if (string.IsNullOrWhiteSpace(actorId) || organization is null || string.IsNullOrWhiteSpace(targetActorId))
            return OrganizationOwnerChangeStatus.Invalid;
        if (!await _ownerAuthorizer.IsActiveOwnerAsync(actorId, organization, cancellationToken).ConfigureAwait(false))
            return OrganizationOwnerChangeStatus.Denied;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var lockedOrganization = await _dbContext.Organizations
            .FromSqlInterpolated($"SELECT * FROM organizations WHERE organization_id = {organization} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (lockedOrganization is null || lockedOrganization.Status != PersistentResourceStatus.Active)
            return OrganizationOwnerChangeStatus.Denied;
        if (!await _ownerAuthorizer.IsActiveOwnerAsync(actorId, organization, cancellationToken).ConfigureAwait(false))
            return OrganizationOwnerChangeStatus.Denied;

        var now = _timeProvider.GetUtcNow();
        var targetMembership = await _dbContext.OrganizationMemberships.AsNoTracking().AnyAsync(membership =>
            membership.OrganizationId == organization && membership.ActorId == targetActorId &&
            membership.Status == PersistentMembershipStatus.Active && membership.ValidFromUtc <= now &&
            (membership.ExpiresAtUtc == null || membership.ExpiresAtUtc > now), cancellationToken).ConfigureAwait(false);
        if (!revoke && !targetMembership) return OrganizationOwnerChangeStatus.Denied;

        var assignment = await _dbContext.OrganizationOwners.SingleOrDefaultAsync(owner =>
            owner.OrganizationId == organization && owner.ActorId == targetActorId && owner.RevokedAtUtc == null,
            cancellationToken).ConfigureAwait(false);
        if (revoke)
        {
            if (assignment is null) return OrganizationOwnerChangeStatus.Unchanged;
            var activeOwnerCount = await _dbContext.OrganizationOwners.CountAsync(owner =>
                owner.OrganizationId == organization && owner.RevokedAtUtc == null &&
                _dbContext.OrganizationMemberships.Any(membership => membership.OrganizationId == organization &&
                    membership.ActorId == owner.ActorId && membership.Status == PersistentMembershipStatus.Active &&
                    membership.ValidFromUtc <= now && (membership.ExpiresAtUtc == null || membership.ExpiresAtUtc > now)),
                cancellationToken).ConfigureAwait(false);
            if (activeOwnerCount <= 1) return OrganizationOwnerChangeStatus.Denied;
            assignment.RevokedAtUtc = now;
            assignment.RevokedByActorId = actorId;
        }
        else
        {
            if (assignment is not null) return OrganizationOwnerChangeStatus.Unchanged;
            _dbContext.OrganizationOwners.Add(new OrganizationOwnerEntity
            {
                OwnerAssignmentId = Guid.NewGuid(), OrganizationId = organization, ActorId = targetActorId,
                AssignedAtUtc = now, AssignedByActorId = actorId
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return OrganizationOwnerChangeStatus.Changed;
    }

    public async Task<OrganizationCreateResult> CreateAsync(
        string actorId,
        string organizationId,
        CancellationToken cancellationToken)
    {
        var organization = NormalizeOrganizationId(organizationId);
        if (string.IsNullOrWhiteSpace(actorId) || organization is null)
        {
            return new OrganizationCreateResult(OrganizationCreateStatus.Invalid);
        }

        try
        {
            var existing = await _dbContext.Organizations
                .AsNoTracking()
                .AnyAsync(candidate => candidate.OrganizationId == organization, cancellationToken)
                .ConfigureAwait(false);
            if (existing)
            {
                return new OrganizationCreateResult(OrganizationCreateStatus.DuplicateId);
            }

            var now = _timeProvider.GetUtcNow();
            _dbContext.Organizations.Add(new OrganizationEntity
            {
                OrganizationId = organization,
                OwnerActorId = actorId,
                Status = PersistentResourceStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            _dbContext.Workspaces.Add(new WorkspaceEntity
            {
                WorkspaceId = organization + "-general",
                OrganizationId = organization,
                Status = PersistentResourceStatus.Active
            });
            var membershipId = Guid.NewGuid();
            _dbContext.OrganizationMemberships.Add(new OrganizationMembershipEntity
            {
                MembershipId = membershipId,
                ActorId = actorId,
                OrganizationId = organization,
                Status = PersistentMembershipStatus.Active,
                ValidFromUtc = now,
                ExpiresAtUtc = null,
                GrantedByActorId = actorId,
                Version = 1
            });
            _dbContext.OrganizationOwners.Add(new OrganizationOwnerEntity
            {
                OwnerAssignmentId = Guid.NewGuid(),
                OrganizationId = organization,
                ActorId = actorId,
                AssignedAtUtc = now,
                AssignedByActorId = actorId
            });
            // SaveChanges commits creator grants with the organization, workspace and membership.
            // Joining an existing organization does not use this provisioning path.
            foreach (var country in new[] { "ZW", "SZ" })
            foreach (var sector in new[] { Sector.HumanHealth, Sector.AnimalHealth })
            {
                var grantId = $"creator-{membershipId:N}-{country}-{sector}";
                _dbContext.AccessGrantGroups.Add(new AccessGrantGroupEntity
                {
                    GrantGroupId = grantId,
                    MembershipId = membershipId,
                    ActorId = actorId,
                    OrganizationId = organization,
                    WorkspaceId = organization + "-general",
                    CountryCode = country,
                    Sector = sector,
                    SensitivityCode = "standard",
                    CanAccessJurisdictionalCases = true,
                    Status = PersistentGrantStatus.Active,
                    ValidFromUtc = now,
                    ExpiresAtUtc = DateTimeOffset.MaxValue,
                    GrantedByActorId = actorId,
                    ChangeReason = "OrganizationCreator",
                    Version = 1
                });
                foreach (var operation in new[] { CaseOperation.ReadCase, CaseOperation.Search, CaseOperation.Count })
                {
                    _dbContext.AccessGrantOperations.Add(new AccessGrantOperationEntity { GrantGroupId = grantId, Operation = operation });
                }
            }
            var epoch = await _dbContext.ActorAccessStates
                .AsNoTracking()
                .Where(state => state.ActorId == actorId)
                .Select(state => (long?)state.AccessEpoch)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            _dbContext.SecurityAuditEntries.Add(new SecurityAuditEntryEntity
            {
                AuditId = Guid.NewGuid(),
                ActorId = actorId,
                Operation = "Organization.Created",
                DecisionCode = "Allowed",
                AccessEpoch = epoch ?? 0,
                ReasonCode = "OrganizationCreator:standard:ZW,SZ:HumanHealth,AnimalHealth:ReadCase,Search,Count",
                RecordedAtUtc = now
            });
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new OrganizationCreateResult(OrganizationCreateStatus.Created, organization);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new OrganizationCreateResult(OrganizationCreateStatus.Unavailable);
        }
    }

    private static string? NormalizeOrganizationId(string? organizationId)
    {
        var trimmed = organizationId?.Trim() ?? string.Empty;
        if (trimmed.Length < 2 || trimmed.Length > 128 || !char.IsAsciiLetterOrDigit(trimmed[0]))
        {
            return null;
        }

        foreach (var character in trimmed)
        {
            var ok = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.';
            if (!ok)
            {
                return null;
            }
        }

        return trimmed;
    }

    public async Task<IReadOnlyList<ActorMembershipRecord>> GetMineAsync(
        string actorId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return [];
        }

        var memberships = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.ActorId == actorId)
            .OrderBy(membership => membership.OrganizationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count == 0)
        {
            return [];
        }

        var organizationIds = memberships.Select(membership => membership.OrganizationId).Distinct().ToList();
        var organizations = await _dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organizationIds.Contains(organization.OrganizationId))
            .ToDictionaryAsync(organization => organization.OrganizationId, cancellationToken)
            .ConfigureAwait(false);
        var workspaces = await _dbContext.WorkspaceOrganizations
            .AsNoTracking()
            .Where(workspace => organizationIds.Contains(workspace.OrganizationId) && workspace.Status == PersistentResourceStatus.Active &&
                _dbContext.Workspaces.Any(scope => scope.WorkspaceId == workspace.WorkspaceId && scope.Status == PersistentResourceStatus.Active))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return memberships.Select(membership => new ActorMembershipRecord(
            membership.OrganizationId,
            organizations.TryGetValue(membership.OrganizationId, out var organization)
                ? organization.Status.ToString()
                : "Unknown",
            membership.Status.ToString(),
            workspaces
                .Where(workspace => workspace.OrganizationId == membership.OrganizationId)
                .Select(workspace => workspace.WorkspaceId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList(),
            membership.ValidFromUtc,
            membership.ExpiresAtUtc)).ToList();
    }

    public async Task<IReadOnlyList<MembershipDirectoryRecord>> SearchAsync(
        string? query,
        CancellationToken cancellationToken)
    {
        var trimmed = query?.Trim();
        var organizations = await _dbContext.Organizations
            .AsNoTracking()
            .Where(organization =>
                organization.Status == PersistentResourceStatus.Active &&
                (string.IsNullOrEmpty(trimmed) || organization.OrganizationId.Contains(trimmed)))
            .OrderBy(organization => organization.OrganizationId)
            .Take(DirectoryLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return organizations
            .Select(organization => new MembershipDirectoryRecord(
                organization.OrganizationId,
                organization.Status.ToString()))
            .ToList();
    }

    public async Task<MembershipRequestSubmitResult> RequestAsync(
        string actorId,
        string organizationId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var organization = organizationId?.Trim() ?? string.Empty;
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(organization) ||
            organization.Length > 128 ||
            trimmedReason?.Length > 500 == true)
        {
            return new MembershipRequestSubmitResult(MembershipRequestSubmitStatus.Invalid);
        }

        try
        {
            var target = await _dbContext.Organizations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.OrganizationId == organization,
                    cancellationToken)
                .ConfigureAwait(false);
            if (target is null || target.Status != PersistentResourceStatus.Active)
            {
                return new MembershipRequestSubmitResult(MembershipRequestSubmitStatus.OrganizationUnavailable);
            }

            var now = _timeProvider.GetUtcNow();
            var expiries = await _dbContext.OrganizationMemberships
                .AsNoTracking()
                .Where(membership => membership.ActorId == actorId &&
                    membership.OrganizationId == organization &&
                    membership.Status == PersistentMembershipStatus.Active)
                .Select(membership => membership.ExpiresAtUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (expiries.Any(expiresAtUtc => expiresAtUtc is null || expiresAtUtc > now))
            {
                return new MembershipRequestSubmitResult(MembershipRequestSubmitStatus.AlreadyMember);
            }

            var pending = await _dbContext.MembershipRequests
                .AsNoTracking()
                .AnyAsync(
                    request => request.ActorId == actorId &&
                        request.OrganizationId == organization &&
                        request.Status == MembershipRequestStatus.Pending,
                    cancellationToken)
                .ConfigureAwait(false);
            if (pending)
            {
                return new MembershipRequestSubmitResult(MembershipRequestSubmitStatus.DuplicatePending);
            }

            var entity = new MembershipRequestEntity
            {
                RequestId = Guid.NewGuid(),
                ActorId = actorId,
                OrganizationId = organization,
                Status = MembershipRequestStatus.Pending,
                Reason = trimmedReason,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _dbContext.MembershipRequests.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new MembershipRequestSubmitResult(
                MembershipRequestSubmitStatus.Submitted,
                new MembershipRequestRecord(
                    entity.RequestId,
                    entity.OrganizationId,
                    entity.Status.ToString(),
                    entity.Reason,
                    entity.CreatedAtUtc));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new MembershipRequestSubmitResult(MembershipRequestSubmitStatus.Unavailable);
        }
    }

    public async Task<IReadOnlyList<MembershipRequestRecord>> GetMyRequestsAsync(
        string actorId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return [];
        }

        return (await _dbContext.MembershipRequests
            .AsNoTracking()
            .Where(request => request.ActorId == actorId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .OrderByDescending(request => request.CreatedAtUtc)
            .Take(DirectoryLimit)
            .Select(request => new MembershipRequestRecord(
                request.RequestId,
                request.OrganizationId,
                request.Status.ToString(),
                request.Reason,
                request.CreatedAtUtc))
            .ToList();
    }
}
