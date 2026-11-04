using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>
/// Lists owned cases or grant-bounded search candidates newest-first by the most
/// recently recorded journal event. Cases without journal rows fall back to the
/// projection timestamp. No sealed payloads are read.
/// </summary>
public sealed class PostgresCaseQueueStore(SwiftAidDbContext dbContext) : ICaseQueueStore, ICaseMapCandidateStore
{
    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAsync(string ownerActorId, int take, CancellationToken ct) =>
        ReadAsync(ownerActorId, take, null, null, null, ct, null, false, null, null, null, null);

    public Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAsync(
        string ownerActorId, int take, CaseQueueFilter filter, CancellationToken ct) =>
        ReadAsync(ownerActorId, take, null, filter.CountryCode, null, ct, filter.Workflow, filter.NewestReportedFirst,
            filter.CaseIds, filter.ReportedFromUtc, filter.ReportedBeforeUtc, null);

    public Task<IReadOnlyList<CaseQueueCandidate>> GetOwnedCasesAfterAsync(
        string ownerActorId, int take, CaseQueueFilter? filter, CaseQueueContinuation? after, CancellationToken ct) =>
        ReadAsync(ownerActorId, take, null, filter?.CountryCode, null, ct, filter?.Workflow,
            filter?.NewestReportedFirst ?? false, filter?.CaseIds, filter?.ReportedFromUtc,
            filter?.ReportedBeforeUtc, after);

    public async Task<IReadOnlyList<CaseQueueCandidate>> GetSearchableCasesAfterAsync(
        string actorId, long accessEpoch, int take, CaseQueueFilter? filter, CaseQueueContinuation? after,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (accessEpoch < 0) throw new ArgumentOutOfRangeException(nameof(accessEpoch));
        if (take < 1) throw new ArgumentOutOfRangeException(nameof(take));

        var now = DateTimeOffset.UtcNow;
        var workflowValue = filter?.Workflow is null ? (SwiftAid.Domain.Cases.CaseWorkflow?)null
            : Enum.Parse<SwiftAid.Domain.Cases.CaseWorkflow>(filter.Workflow);
        var caseIds = filter?.CaseIds;
        var countryCode = filter?.CountryCode;
        var reportedFromUtc = filter?.ReportedFromUtc;
        var reportedBeforeUtc = filter?.ReportedBeforeUtc;
        var searchableGroups = _dbContext.AccessGrantGroups.AsNoTracking()
            .Where(grant => grant.ActorId == actorId && grant.Status == PersistentGrantStatus.Active &&
                grant.ValidFromUtc <= now && grant.ExpiresAtUtc > now &&
                _dbContext.AccessGrantOperations.Any(operation => operation.GrantGroupId == grant.GrantGroupId &&
                    operation.Operation == CaseOperation.Search) &&
                _dbContext.AccessGrantOperations.Any(operation => operation.GrantGroupId == grant.GrantGroupId &&
                    operation.Operation == CaseOperation.ReadCase) &&
                _dbContext.Actors.Any(actor => actor.ActorId == actorId && actor.Status == PersistentActorStatus.Active &&
                    (actor.ExpiresAtUtc == null || actor.ExpiresAtUtc > now)) &&
                _dbContext.ActorAccessStates.Any(state => state.ActorId == actorId && state.AccessEpoch == accessEpoch) &&
                _dbContext.OrganizationMemberships.Any(membership => membership.MembershipId == grant.MembershipId &&
                    membership.ActorId == actorId && membership.OrganizationId == grant.OrganizationId &&
                    membership.Status == PersistentMembershipStatus.Active && membership.ValidFromUtc <= now &&
                    (membership.ExpiresAtUtc == null || membership.ExpiresAtUtc > now)) &&
                _dbContext.Organizations.Any(organization => organization.OrganizationId == grant.OrganizationId &&
                    organization.Status == PersistentResourceStatus.Active) &&
                _dbContext.Workspaces.Any(workspace => workspace.WorkspaceId == grant.WorkspaceId &&
                    workspace.OrganizationId == grant.OrganizationId && workspace.Status == PersistentResourceStatus.Active &&
                    _dbContext.WorkspaceOrganizations.Any(link => link.WorkspaceId == workspace.WorkspaceId &&
                        link.OrganizationId == grant.OrganizationId && link.Status == PersistentResourceStatus.Active)));

        var query = _dbContext.ManagedCases.AsNoTracking()
            .Where(item => caseIds == null || caseIds.Contains(item.CaseId))
            .Where(item => countryCode == null || item.CountryCode == countryCode)
            .Where(item => workflowValue == null || item.Workflow == workflowValue)
            .Where(item => reportedFromUtc == null || item.CreatedAtUtc >= reportedFromUtc)
            .Where(item => reportedBeforeUtc == null || item.CreatedAtUtc < reportedBeforeUtc)
            .Where(item => searchableGroups.Any(grant =>
                grant.OrganizationId == item.OrganizationId && grant.WorkspaceId == item.WorkspaceId &&
                grant.CountryCode == item.CountryCode && grant.Sector == item.Sector &&
                grant.SensitivityCode == item.SensitivityCode &&
                (item.AccountableOwnerActorId == actorId ||
                    _dbContext.CaseShares.Any(share => share.CaseId == item.CaseId && share.ActorId == actorId &&
                        share.IsActive && share.GrantedAtUtc <= now &&
                        (share.ExpiresAtUtc == null || share.ExpiresAtUtc > now) &&
                        (share.AllowedOperations == null ||
                            share.AllowedOperations.Contains(nameof(CaseOperation.Search)) &&
                            share.AllowedOperations.Contains(nameof(CaseOperation.ReadCase)))) ||
                    grant.CanAccessJurisdictionalCases &&
                    _dbContext.ManagedCaseRegions.Any(region => region.CaseId == item.CaseId) &&
                    !_dbContext.ManagedCaseRegions.Any(region => region.CaseId == item.CaseId &&
                        !_dbContext.AccessGrantRegions.Any(grantRegion =>
                            grantRegion.GrantGroupId == grant.GrantGroupId && grantRegion.RegionId == region.RegionId)))));

        var sortByReportTime = filter?.NewestReportedFirst == true;
        var candidates = query.Select(item => new
        {
            item.CaseId,
            Workflow = item.Workflow.ToString(),
            item.Sector,
            item.Classification,
            item.WorkspaceId,
            item.CreatedAtUtc,
            item.CountryCode,
            item.AccountableOwnerActorId,
            item.AggregateVersion,
            item.UpdatedAtUtc,
            LastRecordedAtUtc = _dbContext.CaseEvents
                .Where(journal => journal.CaseId == item.CaseId)
                .Max(journal => (DateTimeOffset?)journal.RecordedAtUtc)
        });
        if (after is not null)
        {
            candidates = candidates.Where(item => (sortByReportTime ? item.CreatedAtUtc : item.LastRecordedAtUtc ?? item.UpdatedAtUtc)
                    < after.SortTimestampUtc ||
                (sortByReportTime ? item.CreatedAtUtc : item.LastRecordedAtUtc ?? item.UpdatedAtUtc)
                    == after.SortTimestampUtc && item.CaseId.CompareTo(after.CaseId) > 0);
        }

        var rows = await candidates
            .OrderByDescending(item => sortByReportTime ? item.CreatedAtUtc : item.LastRecordedAtUtc ?? item.UpdatedAtUtc)
            .ThenBy(item => item.CaseId)
            .Take(take)
            .ToArrayAsync(ct).ConfigureAwait(false);

        return rows.Select(item => new CaseQueueCandidate(item.CaseId, item.Workflow, item.CountryCode,
            item.AccountableOwnerActorId, item.AggregateVersion, item.UpdatedAtUtc,
            item.LastRecordedAtUtc ?? item.UpdatedAtUtc, item.Sector.ToString(), item.Classification.ToString(),
            item.WorkspaceId, item.CreatedAtUtc)).ToArray();
    }

    public Task<IReadOnlyList<CaseQueueCandidate>> GetAsync(string ownerActorId, string workspaceId,
        string countryCode, DateTimeOffset sinceUtc, int take, CancellationToken ct) =>
        ReadAsync(ownerActorId, take, workspaceId, countryCode, sinceUtc, ct, null, false, null, null, null, null);

    private async Task<IReadOnlyList<CaseQueueCandidate>> ReadAsync(
        string ownerActorId, int take, string? workspaceId, string? countryCode, DateTimeOffset? sinceUtc,
        CancellationToken ct, string? workflow, bool newestReportedFirst, IReadOnlyList<Guid>? caseIds,
        DateTimeOffset? reportedFromUtc, DateTimeOffset? reportedBeforeUtc, CaseQueueContinuation? after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActorId);
        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "The queue take must be positive.");
        }

        var workflowValue = workflow is null ? (SwiftAid.Domain.Cases.CaseWorkflow?)null
            : Enum.Parse<SwiftAid.Domain.Cases.CaseWorkflow>(workflow);
        var query = _dbContext.ManagedCases.AsNoTracking()
            .Where(item => item.AccountableOwnerActorId == ownerActorId)
            .Where(item => caseIds == null || caseIds.Contains(item.CaseId))
            .Where(item => workspaceId == null || item.WorkspaceId == workspaceId)
            .Where(item => countryCode == null || item.CountryCode == countryCode)
            .Where(item => workflowValue == null || item.Workflow == workflowValue)
            .Where(item => sinceUtc == null || item.CreatedAtUtc >= sinceUtc)
            .Where(item => reportedFromUtc == null || item.CreatedAtUtc >= reportedFromUtc)
            .Where(item => reportedBeforeUtc == null || item.CreatedAtUtc < reportedBeforeUtc)
            .Select(item => new
            {
                item.CaseId,
                Workflow = item.Workflow.ToString(),
                item.Sector,
                item.Classification,
                item.WorkspaceId,
                item.CreatedAtUtc,
                ReportedAtUtc = (from promotion in _dbContext.IntakePromotions
                                 join report in _dbContext.IntakeReports on promotion.ReceiptId equals report.ReceiptId
                                 where promotion.CaseId == item.CaseId
                                 select (DateTimeOffset?)report.ReceivedAtUtc).FirstOrDefault() ?? item.CreatedAtUtc,
                item.CountryCode,
                item.AccountableOwnerActorId,
                item.AggregateVersion,
                item.UpdatedAtUtc,
                LastRecordedAtUtc = _dbContext.CaseEvents
                    .Where(journal => journal.CaseId == item.CaseId)
                    .Max(journal => (DateTimeOffset?)journal.RecordedAtUtc)
            });
        if (after is not null)
        {
            query = query.Where(item => (newestReportedFirst ? item.ReportedAtUtc : item.LastRecordedAtUtc ?? item.UpdatedAtUtc)
                    < after.SortTimestampUtc ||
                (newestReportedFirst ? item.ReportedAtUtc : item.LastRecordedAtUtc ?? item.UpdatedAtUtc)
                    == after.SortTimestampUtc &&
                item.CaseId.CompareTo(after.CaseId) > 0);
        }

        var rows = await query
            .OrderByDescending(item => newestReportedFirst ? item.ReportedAtUtc : item.LastRecordedAtUtc ?? item.UpdatedAtUtc)
            .ThenBy(item => item.CaseId)
            .Take(take)
            .ToArrayAsync(ct).ConfigureAwait(false);

        return rows
            .Select(item => new CaseQueueCandidate(
                item.CaseId,
                item.Workflow,
                item.CountryCode,
                item.AccountableOwnerActorId,
                item.AggregateVersion,
                item.UpdatedAtUtc,
                item.LastRecordedAtUtc ?? item.UpdatedAtUtc,
                item.Sector.ToString(),
                item.Classification.ToString(), item.WorkspaceId, item.CreatedAtUtc, item.ReportedAtUtc))
            .ToArray();
    }
}
