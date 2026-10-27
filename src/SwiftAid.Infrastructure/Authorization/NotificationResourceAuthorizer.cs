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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (resourceKind != NotificationResourceKind.ManagedCase ||
            action != NotificationAction.OpenCase ||
            !Enum.IsDefined(recipientRequirement) ||
            resourceId == Guid.Empty)
        {
            return false;
        }

        await using var lease = await _unitOfWork.BeginCaseAsync(
            actor,
            resourceId,
            ImmutableArray<string>.Empty,
            CaseLeaseMode.Read,
            cancellationToken).ConfigureAwait(false);

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

        var access = await _caseAccessPolicy.AuthorizeAsync(
            actor,
            resourceId,
            CaseOperation.ReadCase,
            cancellationToken).ConfigureAwait(false);
        if (access.IsAllowed)
        {
            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return access.IsAllowed;
    }
}
