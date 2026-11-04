using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Notifications;
using SwiftAid.Domain.Cases;
using SwiftAid.Domain.Notifications;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Authorization;

/// <summary>Revalidates case ownership, lifecycle, and current case-read authority for notifications.</summary>
public sealed class NotificationResourceAuthorizer : INotificationResourceAuthorizer
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly ICaseAccessPolicy _caseAccessPolicy;
    private readonly ICaseCommandUnitOfWork _unitOfWork;

    public NotificationResourceAuthorizer(
        SwiftAidDbContext dbContext,
        ICaseAccessPolicy caseAccessPolicy,
        ICaseCommandUnitOfWork unitOfWork)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _caseAccessPolicy = caseAccessPolicy ?? throw new ArgumentNullException(nameof(caseAccessPolicy));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async ValueTask<bool> IsCurrentlyAuthorizedAsync(
        ActorContext actor,
        NotificationResourceKind resourceKind,
        Guid resourceId,
        NotificationAction action,
        NotificationRecipientRequirement recipientRequirement,
        CancellationToken cancellationToken,
        Guid? relatedResourceId = null,
        long? relatedResourceVersion = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (resourceKind != NotificationResourceKind.ManagedCase ||
            action != NotificationAction.OpenCase ||
            !Enum.IsDefined(recipientRequirement) ||
            resourceId == Guid.Empty)
        {
            return false;
        }

        if ((recipientRequirement == NotificationRecipientRequirement.CurrentFollowUpAssignee) !=
            (relatedResourceId is not null && relatedResourceVersion is not null))
        {
            return false;
        }

        ICaseCommandLease? lease;
        try
        {
            lease = await _unitOfWork.BeginCaseAsync(
                actor,
                resourceId,
                ImmutableArray<string>.Empty,
                CaseLeaseMode.Read,
                cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // A missing case resource is a denial, not an error, for notification authorization.
            return false;
        }

        await using var _ = lease;

        if (recipientRequirement == NotificationRecipientRequirement.CurrentFollowUpAssignee)
        {
            if (relatedResourceId is not Guid followUpId || relatedResourceVersion is not long scheduleVersion)
            {
                return false;
            }

            var followUp = await _dbContext.ManagedCaseFollowUps.AsNoTracking()
                .Where(item => item.FollowUpId == followUpId && item.CaseId == resourceId &&
                    item.CurrentScheduleVersion == scheduleVersion && item.AssigneeActorId == actor.ActorId &&
                    (item.Status == FollowUpStatus.Scheduled || item.Status == FollowUpStatus.Acknowledged))
                .Select(item => item.FollowUpId)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (followUp == Guid.Empty)
            {
                return false;
            }
        }

        var workflow = await _dbContext.ManagedCases
            .AsNoTracking()
            .Where(item => item.CaseId == resourceId)
            .Select(item => new
            {
                item.Workflow,
                item.AccountableOwnerActorId
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (workflow is null || workflow.Workflow == CaseWorkflow.Resolved ||
            (recipientRequirement == NotificationRecipientRequirement.CurrentAccountableOwner &&
             !StringComparer.Ordinal.Equals(workflow.AccountableOwnerActorId, actor.ActorId)))
        {
            return false;
        }

        var requiredOperation = recipientRequirement == NotificationRecipientRequirement.CurrentFollowUpAssignee
            ? CaseOperation.ManageFollowUp
            : CaseOperation.ReadCase;
        var access = await _caseAccessPolicy.AuthorizeAsync(
            actor,
            resourceId,
            requiredOperation,
            cancellationToken).ConfigureAwait(false);
        if (access.IsAllowed)
        {
            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return access.IsAllowed;
    }
}
