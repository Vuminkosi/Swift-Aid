using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Infrastructure.Persistence.Processing;

public sealed partial class PostgresCaseCommandPersistence
{
    private async Task<bool> MaintainCollaboratorProjectionAsync(
        PostgresCaseCommandLease lease,
        CaseCommandAppendRequest request,
        CaseAggregate next,
        CancellationToken cancellationToken)
    {
        foreach (var change in request.Events)
        {
            if (change is not (CaseCollaboratorGranted or CaseCollaboratorRevoked))
            {
                continue;
            }

            var grantId = change is CaseCollaboratorGranted granted ? granted.GrantId : ((CaseCollaboratorRevoked)change).GrantId;
            var state = next.Collaborators.Single(item => item.GrantId == grantId);
            if (!lease.LockedActorIds.Contains(state.ActorId, StringComparer.Ordinal))
            {
                throw new CasePersistenceRaceException(CasePersistenceRaceKind.AuthorityDiscoveryChanged);
            }

            var share = await _dbContext.CaseShares.SingleOrDefaultAsync(
                item => item.CaseId == lease.CaseId && item.ActorId == state.ActorId, cancellationToken).ConfigureAwait(false);
            if (change is CaseCollaboratorGranted grant)
            {
                // Repeat eligibility after payload preparation and the guarded case update.
                var header = new CaseCommandCaseHeader(lease.CaseId, next.AggregateVersion, next.ReportRevision,
                    request.ExpectedResourceVersion + 1, next.AccountableOwnerId ?? throw InvalidJournal());
                var now = await GetDatabaseUtcNowAsync(lease, cancellationToken).ConfigureAwait(false);
                if (grant.ExpiresAtUtc <= now ||
                    !await IsEligibleCollaboratorAsync(lease, state.ActorId, grant.Actions, header, cancellationToken).ConfigureAwait(false) ||
                    (share is { IsActive: true } && (share.ExpiresAtUtc is null || share.ExpiresAtUtc > now)))
                {
                    return false;
                }

                if (share is null)
                {
                    share = new CaseShareEntity { CaseId = lease.CaseId, ActorId = state.ActorId, GrantedByActorId = change.ActorId };
                    _dbContext.CaseShares.Add(share);
                }

                share.GrantId = grant.GrantId;
                share.Version++;
                share.IsActive = true;
                share.GrantedByActorId = grant.ActorId;
                share.RevokedByActorId = null;
                share.ChangeReason = null; // Narrative remains in the protected event payload.
                share.GrantedAtUtc = grant.RecordedAtUtc;
                share.ExpiresAtUtc = grant.ExpiresAtUtc;
                share.AllowedOperations = Enum.GetValues<CaseCollaborationActions>()
                    .Where(action => grant.Actions.HasFlag(action)).Select(action => action.ToString()).ToArray();
            }
            else
            {
                // Revoking an older expired grant must not revoke its replacement.
                if (share?.GrantId != grantId)
                {
                    continue;
                }

                share.Version++;
                share.IsActive = false;
                share.RevokedByActorId = change.ActorId;
                share.ChangeReason = null;
            }

            await IncrementOwnerEpochsAsync(lease, [state.ActorId], cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
