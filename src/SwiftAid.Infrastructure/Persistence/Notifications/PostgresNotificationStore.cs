using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Notifications;
using SwiftAid.Domain.Notifications;
using WireNotificationAction = SwiftAid.Contracts.Notifications.NotificationAction;
using WireNotificationKind = SwiftAid.Contracts.Notifications.NotificationKind;

namespace SwiftAid.Infrastructure.Persistence.Notifications;

/// <summary>
/// PostgreSQL-backed notification writer and recipient-scoped query. Service wiring and migration
/// ownership remain with the integration owner.
/// </summary>
public sealed class PostgresNotificationStore(
    SwiftAidDbContext dbContext,
    INotificationResourceAuthorizer resourceAuthorizer) : INotificationItemWriter, INotificationItemQuery
{
    private const int MaximumPageSize = 100;

    public async ValueTask<NotificationItem> CreateWithDeliveryOutboxIfMissingAsync(
        Guid acceptedEventId,
        string trustedOccurrenceKey,
        string recipientActorId,
        NotificationKind kind,
        NotificationResourceKind resourceKind,
        Guid resourceId,
        NotificationAction action,
        NotificationRecipientRequirement recipientRequirement,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        var item = NotificationItem.Create(
            acceptedEventId,
            trustedOccurrenceKey,
            recipientActorId,
            kind,
            resourceKind,
            resourceId,
            action,
            recipientRequirement,
            createdAtUtc);

        var items = dbContext.Set<NotificationItem>();
        var outbox = dbContext.Set<NotificationDeliveryOutboxEntity>();
        items.Add(item);
        outbox.Add(new NotificationDeliveryOutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            NotificationId = item.NotificationId,
            CreatedAtUtc = item.CreatedAtUtc,
            AvailableAtUtc = item.CreatedAtUtc
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return item;
        }
        catch (DbUpdateException exception) when (IsOccurrenceConflict(exception))
        {
            dbContext.Entry(item).State = EntityState.Detached;
            foreach (var entry in dbContext.ChangeTracker.Entries<NotificationDeliveryOutboxEntity>()
                         .Where(entry => entry.Entity.NotificationId == item.NotificationId))
            {
                entry.State = EntityState.Detached;
            }

            return await items.AsNoTracking()
                .SingleAsync(
                    existing => existing.RecipientActorId == recipientActorId &&
                                existing.OccurrenceKey == trustedOccurrenceKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<NotificationPage> GetPageAsync(
        ActorContext actor,
        int page,
        int pageSize,
        bool includeDismissed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidatePage(page, pageSize);

        var nowUtc = DateTimeOffset.UtcNow;
        var query = BuildCurrentlyAuthorizedQuery(actor, nowUtc);
        if (!includeDismissed)
        {
            query = query.Where(item => item.DismissedAtUtc == null);
        }

        // This SQL predicate mirrors the shared ReadCase policy for the only currently emitted
        // requirement (CurrentAccountableOwner). Counts and page selection are bounded SQL
        // operations; every returned page item is still rechecked under the current case lease.
        var initialCounts = await CountVisibleItemsAsync(query, cancellationToken).ConfigureAwait(false);
        var totalCount = initialCounts.TotalCount;
        var unreadCount = initialCounts.UnreadCount;

        var offset = checked((page - 1) * pageSize);
        var expectedCandidateCount = Math.Min(pageSize, Math.Max(0, totalCount - offset));
        var candidates = expectedCandidateCount == 0
            ? new List<NotificationItem>()
            : await query
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.NotificationId)
                .Skip(offset)
                .Take(pageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var authorizedItems = new List<SwiftAid.Contracts.Notifications.NotificationItemView>(pageSize);
        var authorizationChangedDuringRead = candidates.Count < expectedCandidateCount;
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await resourceAuthorizer.IsCurrentlyAuthorizedAsync(
                    actor,
                    item.ResourceKind,
                    item.ResourceId,
                    item.Action,
                    item.RecipientRequirement,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                authorizedItems.Add(ToView(item));
            }
            else
            {
                authorizationChangedDuringRead = true;
            }
        }

        if (authorizationChangedDuringRead)
        {
            // A concurrent reassignment or revocation can invalidate rows after the SQL count
            // snapshot. Refresh both exact counts once the lease rechecks have suppressed them.
            var refreshedQuery = BuildCurrentlyAuthorizedQuery(actor, DateTimeOffset.UtcNow);
            if (!includeDismissed)
            {
                refreshedQuery = refreshedQuery.Where(item => item.DismissedAtUtc == null);
            }

            var refreshedCounts = await CountVisibleItemsAsync(refreshedQuery, cancellationToken)
                .ConfigureAwait(false);
            totalCount = refreshedCounts.TotalCount;
            unreadCount = refreshedCounts.UnreadCount;
        }

        return new NotificationPage(
            authorizedItems,
            totalCount,
            unreadCount,
            page,
            pageSize);
    }

    private IQueryable<NotificationItem> BuildCurrentlyAuthorizedQuery(ActorContext actor, DateTimeOffset nowUtc)
    {
        var actorId = actor.ActorId;
        var accessEpoch = actor.AccessEpoch;
        var utcNow = nowUtc.ToUniversalTime();
        var query = dbContext.Set<NotificationItem>()
            .AsNoTracking()
            .Where(item =>
                item.RecipientActorId == actorId &&
                item.ResourceKind == NotificationResourceKind.ManagedCase &&
                item.Action == NotificationAction.OpenCase &&
                item.RecipientRequirement == NotificationRecipientRequirement.CurrentAccountableOwner);

        return query.Where(item =>
            dbContext.Actors.Any(actorRow =>
                actorRow.ActorId == actorId &&
                actorRow.Status == PersistentActorStatus.Active &&
                (actorRow.ExpiresAtUtc == null || actorRow.ExpiresAtUtc > utcNow)) &&
            dbContext.ActorAccessStates.Any(state =>
                state.ActorId == actorId && state.AccessEpoch == accessEpoch) &&
            dbContext.ManagedCases.Any(caseRow =>
                caseRow.CaseId == item.ResourceId &&
                caseRow.AccountableOwnerActorId == actorId &&
                caseRow.Workflow != SwiftAid.Domain.Cases.CaseWorkflow.Resolved &&
                dbContext.ManagedCaseRegions.Any(region => region.CaseId == caseRow.CaseId) &&
                dbContext.Organizations.Any(organization =>
                    organization.OrganizationId == caseRow.OrganizationId &&
                    organization.Status == PersistentResourceStatus.Active) &&
                dbContext.Workspaces.Any(workspace =>
                    workspace.WorkspaceId == caseRow.WorkspaceId &&
                    workspace.OrganizationId == caseRow.OrganizationId &&
                    workspace.Status == PersistentResourceStatus.Active) &&
                dbContext.OrganizationMemberships.Any(membership =>
                    membership.ActorId == actorId &&
                    membership.OrganizationId == caseRow.OrganizationId &&
                    membership.Status == PersistentMembershipStatus.Active &&
                    membership.ValidFromUtc <= utcNow &&
                    (membership.ExpiresAtUtc == null || membership.ExpiresAtUtc > utcNow) &&
                    dbContext.AccessGrantGroups.Any(grant =>
                        grant.ActorId == actorId &&
                        grant.MembershipId == membership.MembershipId &&
                        grant.OrganizationId == caseRow.OrganizationId &&
                        grant.WorkspaceId == caseRow.WorkspaceId &&
                        grant.CountryCode.ToUpper() == caseRow.CountryCode.ToUpper() &&
                        grant.Sector == caseRow.Sector &&
                        grant.SensitivityCode == caseRow.SensitivityCode &&
                        grant.Status == PersistentGrantStatus.Active &&
                        grant.ValidFromUtc <= utcNow &&
                        grant.ExpiresAtUtc > utcNow &&
                        dbContext.AccessGrantOperations.Any(operation =>
                            operation.GrantGroupId == grant.GrantGroupId &&
                            operation.Operation == CaseOperation.ReadCase) &&
                        !dbContext.ManagedCaseRegions.Any(caseRegion =>
                            caseRegion.CaseId == caseRow.CaseId &&
                            !dbContext.AccessGrantRegions.Any(grantRegion =>
                                grantRegion.GrantGroupId == grant.GrantGroupId &&
                                grantRegion.RegionId == caseRegion.RegionId))))));
    }

    private static async ValueTask<NotificationCounts> CountVisibleItemsAsync(
        IQueryable<NotificationItem> query,
        CancellationToken cancellationToken)
    {
        var counts = await query
            .GroupBy(item => item.RecipientActorId)
            .Select(items => new NotificationCounts(
                items.Count(),
                items.Count(item => item.ReadAtUtc == null && item.DismissedAtUtc == null)))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return counts ?? new NotificationCounts(0, 0);
    }

    private sealed record NotificationCounts(int TotalCount, int UnreadCount);

    public ValueTask<bool> MarkReadAsync(
        ActorContext actor,
        Guid notificationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        return SetStateAsync(actor, notificationId, nowUtc, dismiss: false, cancellationToken);
    }

    public ValueTask<bool> DismissAsync(
        ActorContext actor,
        Guid notificationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        return SetStateAsync(actor, notificationId, nowUtc, dismiss: true, cancellationToken);
    }

    private async ValueTask<bool> SetStateAsync(
        ActorContext actor,
        Guid notificationId,
        DateTimeOffset nowUtc,
        bool dismiss,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (notificationId == Guid.Empty)
        {
            return false;
        }

        var item = await dbContext.Set<NotificationItem>()
            .SingleOrDefaultAsync(
                candidate => candidate.NotificationId == notificationId &&
                              candidate.RecipientActorId == actor.ActorId,
                cancellationToken)
            .ConfigureAwait(false);
        if (item is null ||
            !await resourceAuthorizer.IsCurrentlyAuthorizedAsync(
                    actor,
                    item.ResourceKind,
                    item.ResourceId,
                    item.Action,
                    item.RecipientRequirement,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        if (dismiss)
        {
            item.Dismiss(nowUtc);
        }
        else
        {
            item.MarkRead(nowUtc);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static SwiftAid.Contracts.Notifications.NotificationItemView ToView(NotificationItem item)
    {
        var kind = item.Kind switch
        {
            SwiftAid.Domain.Notifications.NotificationKind.CaseNeedsAttention => WireNotificationKind.CaseNeedsAttention,
            _ => throw new InvalidOperationException("An unsupported notification kind was persisted.")
        };
        var action = item.Action switch
        {
            SwiftAid.Domain.Notifications.NotificationAction.OpenCase => WireNotificationAction.OpenAssignedWork,
            _ => throw new InvalidOperationException("An unsupported notification action was persisted.")
        };

        return new SwiftAid.Contracts.Notifications.NotificationItemView(
            item.NotificationId,
            kind,
            item.CreatedAtUtc,
            item.ReadAtUtc,
            item.DismissedAtUtc,
            action,
            item.ResourceId.ToString("D"));
    }

    private static bool IsOccurrenceConflict(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_notification_items_recipient_occurrence"
        };
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        _ = checked((page - 1) * pageSize);
    }
}
