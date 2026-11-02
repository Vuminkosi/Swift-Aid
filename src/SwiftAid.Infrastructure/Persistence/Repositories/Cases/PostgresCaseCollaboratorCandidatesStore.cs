using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

public sealed class PostgresCaseCollaboratorCandidatesStore(SwiftAidDbContext db) : ICaseCollaboratorCandidatesStore
{
    public async Task<CaseCollaboratorCandidatePage> GetAsync(Guid caseId, string search, string? after, int limit, CancellationToken ct)
    {
        var term = search.ToLowerInvariant();
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct).ConfigureAwait(false);
        var candidates = from item in db.ManagedCases.AsNoTracking()
                         join grant in db.AccessGrantGroups.AsNoTracking() on item.OrganizationId equals grant.OrganizationId
                         join member in db.OrganizationMemberships.AsNoTracking() on grant.MembershipId equals member.MembershipId
                         join actor in db.Actors.AsNoTracking() on grant.ActorId equals actor.ActorId
                         join state in db.ActorAccessStates.AsNoTracking() on actor.ActorId equals state.ActorId
                         join profile in db.UserProfiles.AsNoTracking() on actor.ActorId equals profile.UserId into profiles
                         from profile in profiles.DefaultIfEmpty()
                         where item.CaseId == caseId && item.Workflow != CaseWorkflow.Resolved && item.AccountableOwnerActorId != actor.ActorId
                             && grant.WorkspaceId == item.WorkspaceId && grant.CountryCode == item.CountryCode
                             && grant.Sector == item.Sector && grant.SensitivityCode == item.SensitivityCode
                             && grant.Status == PersistentGrantStatus.Active && grant.ValidFromUtc <= now && grant.ExpiresAtUtc > now
                             && member.ActorId == actor.ActorId && member.OrganizationId == item.OrganizationId
                             && member.Status == PersistentMembershipStatus.Active && member.ValidFromUtc <= now
                             && (member.ExpiresAtUtc == null || member.ExpiresAtUtc > now)
                             && actor.Status == PersistentActorStatus.Active && (actor.ExpiresAtUtc == null || actor.ExpiresAtUtc > now)
                             && db.AccessGrantOperations.Any(operation => operation.GrantGroupId == grant.GrantGroupId && operation.Operation == CaseOperation.ReadCase)
                             && !db.CaseShares.Any(share => share.CaseId == caseId && share.ActorId == actor.ActorId && share.IsActive
                                 && (share.ExpiresAtUtc == null || share.ExpiresAtUtc > now))
                             && (term == "" || (profile != null && profile.DisplayName != null && profile.DisplayName.ToLower().Contains(term)))
                         select new { grant.GrantGroupId, actor.ActorId, DisplayName = profile == null ? null : profile.DisplayName };
        if (!string.IsNullOrEmpty(after)) candidates = candidates.Where(item => string.Compare(item.GrantGroupId, after) > 0);
        var rows = await candidates.OrderBy(item => item.GrantGroupId).Take(limit + 1).ToArrayAsync(ct).ConfigureAwait(false);
        var page = rows.Take(limit).ToArray();
        var keys = page.Select(item => item.GrantGroupId).ToArray();
        CaseOperation[] supported = [CaseOperation.ReadCase, CaseOperation.ReadAttachment, CaseOperation.Synchronize,
            CaseOperation.RecordObservation, CaseOperation.ManageFollowUp, CaseOperation.ManageEscalation];
        var operations = await db.AccessGrantOperations.AsNoTracking().Where(item => keys.Contains(item.GrantGroupId) && supported.Contains(item.Operation))
            .ToArrayAsync(ct).ConfigureAwait(false);
        return new CaseCollaboratorCandidatePage(page.Select(item => new CaseCollaboratorCandidate(item.GrantGroupId, item.ActorId, item.DisplayName,
            operations.Where(operation => operation.GrantGroupId == item.GrantGroupId).OrderBy(operation => operation.Operation)
                .Select(operation => operation.Operation.ToString()).ToArray())).ToArray(),
            rows.Length > limit ? page[^1].GrantGroupId : null);
    }
}
