using SwiftAid.Application.Notifications;
using SwiftAid.Domain.Notifications;

namespace SwiftAid.Application.Tests;

public sealed class FollowUpReminderDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_uses_generic_idempotent_occurrence_and_current_schedule_reference()
    {
        var caseId = Guid.NewGuid();
        var followUpId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var store = new ScheduleStore([
            new DueFollowUpReminder(caseId, followUpId, 8, eventId, "assignee-1")]);
        var writer = new CapturingWriter();
        var dispatcher = new FollowUpReminderDispatcher(store, writer);
        var now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

        var count = await dispatcher.DispatchAsync(now, CancellationToken.None);

        Assert.Equal(1, count);
        var occurrence = Assert.Single(writer.Occurrences);
        Assert.Equal(eventId, occurrence.AcceptedEventId);
        Assert.Equal($"case-follow-up:{followUpId:N}:8", occurrence.OccurrenceKey);
        Assert.Equal("assignee-1", occurrence.RecipientActorId);
        Assert.Equal(NotificationKind.CaseNeedsAttention, occurrence.Kind);
        Assert.Equal(NotificationRecipientRequirement.CurrentFollowUpAssignee, occurrence.Requirement);
        Assert.Equal(caseId, occurrence.ResourceId);
        Assert.Equal(followUpId, occurrence.RelatedResourceId);
        Assert.Equal(8, occurrence.RelatedResourceVersion);
        Assert.Equal(now, occurrence.CreatedAtUtc);
    }

    private sealed class ScheduleStore(IReadOnlyList<DueFollowUpReminder> due) : IFollowUpReminderScheduleStore
    {
        public Task<IReadOnlyList<DueFollowUpReminder>> GetDueAsync(
            DateTimeOffset nowUtc,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(due);
    }

    private sealed class CapturingWriter : INotificationItemWriter
    {
        public List<Occurrence> Occurrences { get; } = [];

        public ValueTask<NotificationItem> CreateWithDeliveryOutboxIfMissingAsync(
            Guid acceptedEventId,
            string trustedOccurrenceKey,
            string recipientActorId,
            NotificationKind kind,
            NotificationResourceKind resourceKind,
            Guid resourceId,
            NotificationAction action,
            NotificationRecipientRequirement recipientRequirement,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken,
            Guid? relatedResourceId = null,
            long? relatedResourceVersion = null)
        {
            Occurrences.Add(new Occurrence(acceptedEventId, trustedOccurrenceKey, recipientActorId, kind,
                resourceId, recipientRequirement, createdAtUtc, relatedResourceId, relatedResourceVersion));
            return ValueTask.FromResult(NotificationItem.Create(acceptedEventId, trustedOccurrenceKey,
                recipientActorId, kind, resourceKind, resourceId, action, recipientRequirement, createdAtUtc,
                relatedResourceId, relatedResourceVersion));
        }
    }

    private sealed record Occurrence(
        Guid AcceptedEventId,
        string OccurrenceKey,
        string RecipientActorId,
        NotificationKind Kind,
        Guid ResourceId,
        NotificationRecipientRequirement Requirement,
        DateTimeOffset CreatedAtUtc,
        Guid? RelatedResourceId,
        long? RelatedResourceVersion);
}
