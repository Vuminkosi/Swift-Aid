using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Notifications;
using SwiftAid.Domain.Cases;
using SwiftAid.Domain.Notifications;

namespace SwiftAid.Infrastructure.Persistence.Notifications;

/// <summary>
/// Reuses the authenticated case authorization evaluator for trusted delivery after loading the
/// recipient and access epoch from PostgreSQL. It never synthesizes a principal or trusts a token.
/// </summary>
public sealed class PostgresTrustedNotificationRecipientAuthorizer : ITrustedNotificationRecipientAuthorizer
{
    private readonly SwiftAidDbContext _dbContext;
    private readonly ICaseCommandUnitOfWork _caseUnitOfWork;
    private readonly ICaseAccessPolicy _caseAccessPolicy;
    private readonly TimeProvider _timeProvider;

    public PostgresTrustedNotificationRecipientAuthorizer(
        SwiftAidDbContext dbContext,
        ICaseCommandUnitOfWork caseUnitOfWork,
        ICaseAccessPolicy caseAccessPolicy,
        TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _caseUnitOfWork = caseUnitOfWork ?? throw new ArgumentNullException(nameof(caseUnitOfWork));
        _caseAccessPolicy = caseAccessPolicy ?? throw new ArgumentNullException(nameof(caseAccessPolicy));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<TrustedNotificationAuthorizationStatus> AuthorizeCurrentAsync(
        string recipientActorId,
        NotificationResourceKind resourceKind,
        Guid resourceId,
        NotificationAction action,
        NotificationRecipientRequirement recipientRequirement,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipientActorId) ||
            !string.Equals(recipientActorId, recipientActorId.Trim(), StringComparison.Ordinal) ||
            recipientActorId.Length > 512 ||
            resourceKind != NotificationResourceKind.ManagedCase ||
            action != NotificationAction.OpenCase ||
            !Enum.IsDefined(recipientRequirement) ||
            resourceId == Guid.Empty)
        {
            return TrustedNotificationAuthorizationStatus.Suppressed;
        }

        try
        {
            var initialState = await ReadCurrentActorStateAsync(recipientActorId, cancellationToken)
                .ConfigureAwait(false);
            if (initialState is null || !initialState.IsActive)
            {
                return TrustedNotificationAuthorizationStatus.Suppressed;
            }

            var actor = ActorContext.FromTrustedAuthorizationState(initialState);
            await using var lease = await _caseUnitOfWork.BeginCaseAsync(
                    actor,
                    resourceId,
                    ImmutableArray<string>.Empty,
                    CaseLeaseMode.Read,
                    cancellationToken)
                .ConfigureAwait(false);

            // Re-read while the case and recipient authority are protected by
            // the case read lease. The same evaluator used by authenticated requests consumes this
            // current actor epoch and the normal PostgreSQL grant/resource stores.
            var currentState = await ReadCurrentActorStateAsync(recipientActorId, cancellationToken)
                .ConfigureAwait(false);
            if (currentState is null || !currentState.IsActive)
            {
                await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
                return TrustedNotificationAuthorizationStatus.Suppressed;
            }

            actor = ActorContext.FromTrustedAuthorizationState(currentState);
            var resource = await _dbContext.ManagedCases
                .AsNoTracking()
                .Where(item => item.CaseId == resourceId)
                .Select(item => new { item.Workflow, item.AccountableOwnerActorId })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (resource is null ||
                resource.Workflow == CaseWorkflow.Resolved ||
                (recipientRequirement == NotificationRecipientRequirement.CurrentAccountableOwner &&
                 !string.Equals(resource.AccountableOwnerActorId, recipientActorId, StringComparison.Ordinal)))
            {
                await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
                return TrustedNotificationAuthorizationStatus.Suppressed;
            }

            var decision = await _caseAccessPolicy.AuthorizeAsync(
                    actor,
                    resourceId,
                    CaseOperation.ReadCase,
                    cancellationToken)
                .ConfigureAwait(false);

            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (decision.IsAllowed)
            {
                return TrustedNotificationAuthorizationStatus.Authorized;
            }

            return decision.Code is AccessDecisionCode.GrantSourceUnavailable or AccessDecisionCode.ResourceSourceUnavailable
                ? TrustedNotificationAuthorizationStatus.Unavailable
                : TrustedNotificationAuthorizationStatus.Suppressed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            return TrustedNotificationAuthorizationStatus.Suppressed;
        }
        catch (UnauthorizedAccessException)
        {
            return TrustedNotificationAuthorizationStatus.Suppressed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return TrustedNotificationAuthorizationStatus.Unavailable;
        }
    }

    private async ValueTask<TrustedActorAuthorizationState?> ReadCurrentActorStateAsync(
        string actorId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var state = await (
                from actor in _dbContext.Actors.AsNoTracking()
                join access in _dbContext.ActorAccessStates.AsNoTracking()
                    on actor.ActorId equals access.ActorId
                where actor.ActorId == actorId
                select new
                {
                    actor.ActorId,
                    actor.Status,
                    actor.ExpiresAtUtc,
                    access.AccessEpoch
                })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (state is null)
        {
            return null;
        }

        var active = state.Status == PersistentActorStatus.Active &&
            state.AccessEpoch >= 0 &&
            (state.ExpiresAtUtc is null || state.ExpiresAtUtc > now);
        return new TrustedActorAuthorizationState(
            state.ActorId,
            state.AccessEpoch,
            active,
            state.ExpiresAtUtc);
    }
}
