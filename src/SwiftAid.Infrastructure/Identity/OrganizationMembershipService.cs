using Microsoft.EntityFrameworkCore;
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

public interface IOrganizationMembershipService
{
    Task<IReadOnlyList<ActorMembershipRecord>> GetMineAsync(string actorId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MembershipDirectoryRecord>> SearchAsync(string? query, CancellationToken cancellationToken);

    Task<MembershipRequestSubmitResult> RequestAsync(
        string actorId,
        string organizationId,
        string? reason,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MembershipRequestRecord>> GetMyRequestsAsync(string actorId, CancellationToken cancellationToken);
}

/// <summary>Member-side organization access: read memberships, browse the directory,
/// request to join. Review and grants stay with staff tooling and authority tables.</summary>
public sealed class OrganizationMembershipService(
    SwiftAidDbContext dbContext,
    TimeProvider timeProvider) : IOrganizationMembershipService
{
    private const int DirectoryLimit = 25;

    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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
        var workspaces = await _dbContext.Workspaces
            .AsNoTracking()
            .Where(workspace => organizationIds.Contains(workspace.OrganizationId))
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
