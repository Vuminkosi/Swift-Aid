using SwiftAid.Domain.Notifications;

namespace SwiftAid.Application.Notifications;

public sealed record DueFollowUpReminder(
    Guid CaseId,
    Guid FollowUpId,
    long ScheduleVersion,
    Guid ScheduleEventId,
    string AssigneeActorId);

public interface IFollowUpReminderScheduleStore
{
    /// <summary>Returns due schedules whose current recipient occurrence has not been materialized.</summary>
    Task<IReadOnlyList<DueFollowUpReminder>> GetDueAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>Materializes durable due follow-ups as idempotent generic notification occurrences.</summary>
public sealed class FollowUpReminderDispatcher(
    IFollowUpReminderScheduleStore schedules,
    INotificationItemWriter notificationWriter)
{
    private const int MaximumBatchSize = 100;

    public async Task<int> DispatchAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();
        var due = await schedules.GetDueAsync(now, MaximumBatchSize, cancellationToken)
            .ConfigureAwait(false);

        foreach (var reminder in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var occurrenceKey = $"case-follow-up:{reminder.FollowUpId:N}:{reminder.ScheduleVersion}";
            await notificationWriter.CreateWithDeliveryOutboxIfMissingAsync(
                    reminder.ScheduleEventId,
                    occurrenceKey,
                    reminder.AssigneeActorId,
                    NotificationKind.CaseNeedsAttention,
                    NotificationResourceKind.ManagedCase,
                    reminder.CaseId,
                    NotificationAction.OpenCase,
                    NotificationRecipientRequirement.CurrentFollowUpAssignee,
                    now,
                    cancellationToken,
                    reminder.FollowUpId,
                    reminder.ScheduleVersion)
                .ConfigureAwait(false);
        }

        return due.Count;
    }
}
