using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Notifications;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Cases;

/// <summary>Reads due open reminders from the authoritative follow-up projection.</summary>
public sealed class PostgresFollowUpReminderScheduleStore(SwiftAidDbContext dbContext)
    : IFollowUpReminderScheduleStore
{
    public async Task<IReadOnlyList<DueFollowUpReminder>> GetDueAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return await dbContext.ManagedCaseFollowUps
            .AsNoTracking()
            .Where(item => item.DueAtUtc <= nowUtc &&
                (item.Status == FollowUpStatus.Scheduled || item.Status == FollowUpStatus.Acknowledged))
            .OrderBy(item => item.DueAtUtc)
            .ThenBy(item => item.FollowUpId)
            .Take(limit)
            .Select(item => new DueFollowUpReminder(
                item.CaseId,
                item.FollowUpId,
                item.CurrentScheduleVersion,
                item.CurrentScheduleEventId,
                item.AssigneeActorId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
