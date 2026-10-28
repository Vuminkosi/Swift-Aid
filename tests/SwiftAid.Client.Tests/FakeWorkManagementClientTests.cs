using SwiftAid.Client.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class FakeWorkManagementClientTests
{
    private static readonly Guid CaseOneId = Guid.Parse("a5a8e6ca-83b0-4b75-8ec8-ecab1e680101");
    private static readonly Guid CaseTwoId = Guid.Parse("a5a8e6ca-83b0-4b75-8ec8-ecab1e680102");

    [Fact]
    public async Task AssignmentCanRequestAssistanceAndProvidedRemainsDistinctFromReceiptConfirmation()
    {
        var client = CreateClient();
        var query = Query();
        var initial = await GetAsync(client, CaseTwoId, query);
        var assignment = Item(initial, WorkManagementKind.Assignment);
        var workerKey = SelectWorker(assignment);

        var assigned = await ExecuteAsync(client, initial, assignment, WorkManagementAction.AssignWorker, workerKey: workerKey);
        Assert.Equal(WorkManagementStatus.Assigned, Item(assigned, WorkManagementKind.Assignment).Status);

        var currentAssignment = Item(assigned, WorkManagementKind.Assignment);
        var requested = await ExecuteAsync(
            client,
            assigned,
            currentAssignment,
            WorkManagementAction.RequestAssistance,
            assistanceNeed: WorkManagementAssistanceNeed.Transport,
            details: "Sample transport coordination");
        var assistance = Item(requested, WorkManagementKind.Assistance);
        Assert.Equal(WorkManagementStatus.Requested, assistance.Status);

        var providedSnapshot = await ExecuteAsync(
            client,
            requested,
            assistance,
            WorkManagementAction.RecordAssistanceProvided,
            assistanceNeed: WorkManagementAssistanceNeed.Transport,
            details: "Sample transport support provided");
        var provided = Item(providedSnapshot, WorkManagementKind.Assistance);
        Assert.Equal(WorkManagementStatus.Provided, provided.Status);
        Assert.Contains(WorkManagementAction.ConfirmAssistanceReceived, provided.AvailableActions);
        Assert.Contains(provided.History, entry => entry.Action == WorkManagementAction.RecordAssistanceProvided);

        var receivedSnapshot = await ExecuteAsync(
            client,
            providedSnapshot,
            provided,
            WorkManagementAction.ConfirmAssistanceReceived,
            assistanceReceiptConfirmed: true);
        var received = Item(receivedSnapshot, WorkManagementKind.Assistance);
        Assert.Equal(WorkManagementStatus.ReceiptConfirmed, received.Status);
        Assert.NotEqual(provided.Status, received.Status);
        Assert.Contains(received.History, entry => entry.Action == WorkManagementAction.ConfirmAssistanceReceived && entry.AssistanceReceiptConfirmed);
        Assert.Empty(received.AvailableActions);
    }

    [Fact]
    public async Task NoContactFollowUpRemainsMissedInsteadOfCompleted()
    {
        var client = CreateClient();
        var query = Query();
        var snapshot = await GetAsync(client, CaseTwoId, query);
        var followUp = Assert.Single(snapshot.Items.Items, item => item.Kind == WorkManagementKind.FollowUp && item.Status == WorkManagementStatus.Missed);

        var result = await ExecuteAsync(
            client,
            snapshot,
            followUp,
            WorkManagementAction.RecordFollowUpOutcome,
            followUpOutcome: WorkManagementFollowUpOutcome.NoContact,
            details: "No contact was made in this sample");

        var updated = Item(result, WorkManagementKind.FollowUp, followUp.WorkKey);
        Assert.Equal(WorkManagementStatus.Missed, updated.Status);
        Assert.NotEqual(WorkManagementStatus.Completed, updated.Status);
        Assert.Contains(updated.History, entry => entry.Action == WorkManagementAction.RecordFollowUpOutcome &&
            entry.FollowUpOutcome == WorkManagementFollowUpOutcome.NoContact);
    }

    [Fact]
    public async Task FollowUpRescheduleAndCancelPreserveDueDateHistory()
    {
        var client = CreateClient();
        var query = Query();
        var snapshot = await GetAsync(client, CaseOneId, query);
        var followUp = Assert.Single(snapshot.Items.Items, item => item.Kind == WorkManagementKind.FollowUp);
        var originalDue = Assert.IsType<DateTimeOffset>(followUp.DueAtUtc);
        var newDue = DateTimeOffset.UtcNow.AddDays(5);
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(newDue.UtcDateTime);

        var rescheduledSnapshot = await ExecuteAsync(
            client,
            snapshot,
            followUp,
            WorkManagementAction.RescheduleFollowUp,
            dueAtUtc: originalDue,
            newDueAtUtc: newDue,
            localTimeZoneId: TimeZoneInfo.Local.Id,
            localOffset: localOffset);
        var rescheduled = Item(rescheduledSnapshot, WorkManagementKind.FollowUp, followUp.WorkKey);
        Assert.Equal(WorkManagementStatus.Rescheduled, rescheduled.Status);
        Assert.Equal(newDue, rescheduled.DueAtUtc);
        var rescheduleEntry = Assert.Single(rescheduled.History, entry => entry.Action == WorkManagementAction.RescheduleFollowUp);
        Assert.Equal(originalDue, rescheduleEntry.DueAtUtc);
        Assert.Equal(newDue, rescheduleEntry.NewDueAtUtc);
        Assert.Equal(TimeZoneInfo.Local.Id, rescheduleEntry.LocalTimeZoneId);
        Assert.Equal(localOffset, rescheduleEntry.LocalOffset);

        var cancelledSnapshot = await ExecuteAsync(
            client,
            rescheduledSnapshot,
            rescheduled,
            WorkManagementAction.CancelFollowUp,
            dueAtUtc: newDue,
            cancellationReason: "Sample scheduling change");
        var cancelled = Item(cancelledSnapshot, WorkManagementKind.FollowUp, followUp.WorkKey);
        Assert.Equal(WorkManagementStatus.Cancelled, cancelled.Status);
        var cancellationEntry = Assert.Single(cancelled.History, entry => entry.Action == WorkManagementAction.CancelFollowUp);
        Assert.Equal(newDue, cancellationEntry.DueAtUtc);
        Assert.Equal("Sample scheduling change", cancellationEntry.CancellationReason);
        Assert.Empty(cancelled.AvailableActions);
    }

    [Fact]
    public async Task EscalationCanBeAcknowledgedActedOnAndResolvedWithTypedHistory()
    {
        var client = CreateClient();
        var query = Query();
        var snapshot = await GetAsync(client, CaseOneId, query);
        var escalation = Assert.Single(snapshot.Items.Items, item => item.Kind == WorkManagementKind.Escalation);

        var acknowledgedSnapshot = await ExecuteAsync(client, snapshot, escalation, WorkManagementAction.AcknowledgeEscalation);
        var acknowledged = Item(acknowledgedSnapshot, WorkManagementKind.Escalation, escalation.WorkKey);
        Assert.Equal(WorkManagementStatus.Acknowledged, acknowledged.Status);

        var actionSnapshot = await ExecuteAsync(
            client,
            acknowledgedSnapshot,
            acknowledged,
            WorkManagementAction.RecordEscalationAction,
            escalationAction: WorkManagementEscalationAction.CoordinatedWithTeam,
            details: "Sample coordination recorded");
        var actioned = Item(actionSnapshot, WorkManagementKind.Escalation, escalation.WorkKey);
        Assert.Equal(WorkManagementStatus.Acknowledged, actioned.Status);
        Assert.Contains(actioned.History, entry => entry.Action == WorkManagementAction.RecordEscalationAction &&
            entry.EscalationAction == WorkManagementEscalationAction.CoordinatedWithTeam);

        var resolvedSnapshot = await ExecuteAsync(
            client,
            actionSnapshot,
            actioned,
            WorkManagementAction.ResolveEscalation,
            reason: "Sample escalation resolved",
            resolutionSummary: "The sample coordination is complete");
        var resolved = Item(resolvedSnapshot, WorkManagementKind.Escalation, escalation.WorkKey);
        Assert.Equal(WorkManagementStatus.Resolved, resolved.Status);
        Assert.Contains(resolved.History, entry => entry.Action == WorkManagementAction.ResolveEscalation &&
            entry.Reason == "Sample escalation resolved" &&
            entry.ResolutionSummary == "The sample coordination is complete");
        Assert.Empty(resolved.AvailableActions);
    }

    [Fact]
    public async Task InvalidEnumsPageBoundsAndContradictoryPayloadAreRejectedWithoutMutation()
    {
        var client = CreateClient();
        var query = Query();
        var original = await GetAsync(client, CaseOneId, query);

        var invalidKind = await client.GetAsync(CaseOneId, new WorkManagementQuery(new PageRequest(1, 10), (WorkManagementKind)999));
        Assert.Equal(UiCapabilityResultCode.InvalidRequest, invalidKind.Code);
        Assert.Null(invalidKind.Value);

        foreach (var page in new[] { new PageRequest(0, 10), new PageRequest(1, 101) })
        {
            var invalidPage = await client.GetAsync(CaseOneId, new WorkManagementQuery(page));
            Assert.Equal(UiCapabilityResultCode.InvalidRequest, invalidPage.Code);
            Assert.Null(invalidPage.Value);
        }

        var assignment = Item(original, WorkManagementKind.Assignment);
        var undefinedAction = await client.ExecuteAsync(
            new WorkManagementCommand(CaseOneId, assignment.WorkKey, (WorkManagementAction)999, original.Version),
            query);
        Assert.Equal(UiCapabilityResultCode.InvalidRequest, undefinedAction.Code);
        Assert.Null(undefinedAction.Value);

        var contradictoryPayload = await client.ExecuteAsync(
            new WorkManagementCommand(
                CaseOneId,
                assignment.WorkKey,
                WorkManagementAction.ReassignWorker,
                original.Version,
                WorkerKey: SelectWorker(assignment),
                Details: "Unexpected field for assignment"),
            query);
        Assert.Equal(UiCapabilityResultCode.InvalidRequest, contradictoryPayload.Code);
        Assert.Null(contradictoryPayload.Value);

        var after = await GetAsync(client, CaseOneId, query);
        Assert.Equal(original.Version, after.Version);
        var unchangedAssignment = Item(after, WorkManagementKind.Assignment);
        Assert.Equal(Item(original, WorkManagementKind.Assignment).Status, unchangedAssignment.Status);
        Assert.Equal(Item(original, WorkManagementKind.Assignment).AvailableActions, unchangedAssignment.AvailableActions);
        Assert.Equal(Item(original, WorkManagementKind.Assignment).History.Count, unchangedAssignment.History.Count);
    }

    [Fact]
    public async Task NegativeAndStaleVersionsAndUnavailableActionsDoNotMutateWork()
    {
        var client = CreateClient();
        var query = Query();
        var original = await GetAsync(client, CaseTwoId, query);
        var assignment = Item(original, WorkManagementKind.Assignment);
        var workerKey = SelectWorker(assignment);

        var negative = await client.ExecuteAsync(
            new WorkManagementCommand(CaseTwoId, assignment.WorkKey, WorkManagementAction.AssignWorker, -1, workerKey),
            query);
        Assert.Equal(UiCapabilityResultCode.InvalidRequest, negative.Code);
        Assert.Null(negative.Value);

        var stale = await client.ExecuteAsync(
            new WorkManagementCommand(CaseTwoId, assignment.WorkKey, WorkManagementAction.AssignWorker, original.Version + 1, workerKey),
            query);
        Assert.Equal(UiCapabilityResultCode.Conflict, stale.Code);
        Assert.Equal(original.Version, stale.Value!.Version);
        Assert.Equal(WorkManagementStatus.AcknowledgementPending, Item(stale.Value, WorkManagementKind.Assignment).Status);

        var disallowed = await client.ExecuteAsync(
            new WorkManagementCommand(CaseTwoId, assignment.WorkKey, WorkManagementAction.ReassignWorker, original.Version, workerKey),
            query);
        Assert.Equal(UiCapabilityResultCode.Conflict, disallowed.Code);
        Assert.Equal(original.Version, disallowed.Value!.Version);

        var unchanged = await GetAsync(client, CaseTwoId, query);
        Assert.Equal(original.Version, unchanged.Version);
        Assert.Equal(WorkManagementStatus.AcknowledgementPending, Item(unchanged, WorkManagementKind.Assignment).Status);
    }

    [Fact]
    public async Task CancellationIsObservedBeforeReadOrMutation()
    {
        var client = CreateClient();
        var query = Query();
        var before = await GetAsync(client, CaseTwoId, query);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.GetAsync(CaseTwoId, query, cancellation.Token));

        var assignment = Item(before, WorkManagementKind.Assignment);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.ExecuteAsync(
                new WorkManagementCommand(CaseTwoId, assignment.WorkKey, WorkManagementAction.AssignWorker, before.Version,
                    SelectWorker(assignment)),
                query,
                cancellation.Token));

        var after = await GetAsync(client, CaseTwoId, query);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(WorkManagementStatus.AcknowledgementPending, Item(after, WorkManagementKind.Assignment).Status);
    }

    private static FakeWorkManagementClient CreateClient() => new(new FakeCaseStore());

    private static string SelectWorker(WorkManagementItem assignment)
    {
        const string workerKey = "sample-worker-01";
        Assert.Contains(assignment.EligibleWorkers, worker => worker.Key == workerKey);
        return workerKey;
    }

    private static WorkManagementQuery Query() => new(new PageRequest(1, 100));

    private static async Task<WorkManagementSnapshot> GetAsync(
        FakeWorkManagementClient client,
        Guid caseId,
        WorkManagementQuery query)
    {
        var result = await client.GetAsync(caseId, query);
        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.NotNull(result.Value);
        return result.Value!;
    }

    private static async Task<WorkManagementSnapshot> ExecuteAsync(
        FakeWorkManagementClient client,
        WorkManagementSnapshot current,
        WorkManagementItem item,
        WorkManagementAction action,
        string? workerKey = null,
        WorkManagementAssistanceNeed? assistanceNeed = null,
        string? details = null,
        bool assistanceReceiptConfirmed = false,
        WorkManagementFollowUpOutcome? followUpOutcome = null,
        DateTimeOffset? dueAtUtc = null,
        DateTimeOffset? newDueAtUtc = null,
        string? localTimeZoneId = null,
        TimeSpan? localOffset = null,
        string? cancellationReason = null,
        WorkManagementEscalationAction? escalationAction = null,
        string? reason = null,
        string? resolutionSummary = null)
    {
        var result = await client.ExecuteAsync(
            new WorkManagementCommand(
                current.CaseId,
                item.WorkKey,
                action,
                current.Version,
                workerKey,
                assistanceNeed,
                details,
                followUpOutcome,
                EscalationAction: escalationAction,
                AssistanceReceiptConfirmed: assistanceReceiptConfirmed,
                DueAtUtc: dueAtUtc,
                NewDueAtUtc: newDueAtUtc,
                LocalTimeZoneId: localTimeZoneId,
                LocalOffset: localOffset,
                CancellationReason: cancellationReason,
                Reason: reason,
                ResolutionSummary: resolutionSummary),
            Query());
        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.NotNull(result.Value);
        return result.Value!;
    }

    private static WorkManagementItem Item(
        WorkManagementSnapshot snapshot,
        WorkManagementKind kind,
        string? workKey = null) =>
        Assert.Single(snapshot.Items.Items, item => item.Kind == kind && (workKey is null || item.WorkKey == workKey));
}
