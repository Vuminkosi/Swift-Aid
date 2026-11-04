using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases;

public interface ICaseCollaboratorCandidatesStore
{
    Task<CaseCollaboratorCandidatePage> GetAsync(Guid caseId, string search, string? after, int limit, CancellationToken ct, bool followUpAssignees = false);
}

public interface ICaseCollaboratorCandidatesQuery
{
    /// <summary>Null means denied or absent. Search and cursor do not grant authority.</summary>
    Task<CaseCollaboratorCandidatePage?> GetAsync(ActorContext actor, Guid caseId, string? search, string? after, int limit, CancellationToken ct, bool followUpAssignees = false);
}

public sealed class CaseCollaboratorCandidatesQuery(
    ICaseCollaboratorCandidatesStore store, ICaseAccessPolicy policy, ICaseCommandUnitOfWork unitOfWork) : ICaseCollaboratorCandidatesQuery
{
    public async Task<CaseCollaboratorCandidatePage?> GetAsync(ActorContext actor, Guid caseId, string? search, string? after, int limit, CancellationToken ct, bool followUpAssignees = false)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (limit is < 1 or > 50 || search?.Length > 120 || after?.Length > 200 ||
            search?.Any(char.IsControl) == true || after?.Any(char.IsControl) == true)
            throw new ArgumentException("The candidate query is invalid.");
        if (caseId == Guid.Empty) return null;
        ICaseCommandLease lease;
        try
        {
            lease = await unitOfWork.BeginCaseAsync(actor, caseId, [], CaseLeaseMode.Read, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException) { return null; }
        await using (lease.ConfigureAwait(false))
        {
            var operation = followUpAssignees ? CaseOperation.ManageFollowUp : CaseOperation.ManageCollaborators;
            var decision = await policy.AuthorizeAllAsync(actor, caseId,
                [CaseOperation.ReadCase, operation], ct).ConfigureAwait(false);
            if (!decision.IsAllowed) return null;
            var page = await store.GetAsync(caseId, search?.Trim() ?? "", after, limit, ct, followUpAssignees).ConfigureAwait(false);
            await lease.CommitAsync(ct).ConfigureAwait(false);
            return page;
        }
    }
}
