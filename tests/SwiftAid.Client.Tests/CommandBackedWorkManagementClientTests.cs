using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Tests;

public sealed class CommandBackedWorkManagementClientTests
{
    [Fact]
    public async Task Assignment_query_and_post_action_refresh_do_not_depend_on_monitoring()
    {
        var header = Header() with { AssignmentVersion = 2, AssignmentRecordedAtUtc = DateTimeOffset.UtcNow,
            CanAcknowledgeAssignment = true };
        var commands = new StubCommandClient();
        var monitoring = new UnavailableMonitoringClient();
        var client = new CommandBackedWorkManagementClient(commands, monitoring,
            new StubTimelineClient(timeline: new CaseTimeline(header, [])));
        var query = new WorkManagementQuery(new PageRequest(1, 25), WorkManagementKind.Assignment);
        var result = await client.GetAsync(CaseId, query);
        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        var item = Assert.Single(result.Value!.Items.Items);
        Assert.Contains(WorkManagementAction.AcknowledgeAssignment, item.AvailableActions);
        var accepted = await client.ExecuteAsync(new WorkManagementCommand(CaseId, item.WorkKey,
            WorkManagementAction.AcknowledgeAssignment, 7), query);
        Assert.Equal(UiCapabilityResultCode.Success, accepted.Code);
        Assert.Single(commands.Sent);
        Assert.Equal(0, monitoring.Calls);

        var filtered = await client.GetAsync(CaseId, query with { Status = WorkManagementStatus.Acknowledged });
        Assert.Empty(filtered.Value!.Items.Items);
        var secondPage = await client.GetAsync(CaseId, query with { Page = new PageRequest(2, 25) });
        Assert.Empty(secondPage.Value!.Items.Items);
        Assert.Equal(1, secondPage.Value.Items.TotalCount);
    }

    private sealed class UnavailableMonitoringClient : IMonitoringQueueClient
    {
        public int Calls { get; private set; }
        public Task<MonitoringQueueResult> GetAssignedAsync(int? limit, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new HttpRequestException("Synthetic monitoring outage.");
        }
        public Task<MonitoringQueueResult> GetOpenAsync(int? limit, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new HttpRequestException("Synthetic monitoring outage.");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Assignment_projects_pending_or_acknowledged_with_only_authorized_action(bool acknowledged, bool allowed)
    {
        var header = Header() with { AssignmentVersion = 2, AssignmentAcknowledgedAtUtc = acknowledged ? DateTimeOffset.UtcNow : null,
            CanAcknowledgeAssignment = allowed, AssignmentRecordedAtUtc = DateTimeOffset.UtcNow.AddHours(-1) };
        var commands = new StubCommandClient();
        var client = new CommandBackedWorkManagementClient(commands, new StubMonitoringClient(),
            new StubTimelineClient(timeline: new CaseTimeline(header, [])));
        var query = new WorkManagementQuery(new PageRequest(1, 25));
        var result = await client.GetAsync(CaseId, query);
        var assignment = Assert.Single(result.Value!.Items.Items, item => item.Kind == WorkManagementKind.Assignment);
        Assert.Equal(acknowledged ? WorkManagementStatus.Acknowledged : WorkManagementStatus.AcknowledgementPending, assignment.Status);
        Assert.Equal(allowed && !acknowledged, assignment.CanAcknowledgeAssignment);
        Assert.Equal(allowed && !acknowledged, assignment.AvailableActions.Contains(WorkManagementAction.AcknowledgeAssignment));
        if (allowed && !acknowledged)
        {
            await client.ExecuteAsync(new WorkManagementCommand(CaseId, assignment.WorkKey, WorkManagementAction.AcknowledgeAssignment, 7), query);
            var sent = Assert.Single(commands.Sent).Input;
            Assert.Equal("AcknowledgeAssignment", sent.CommandType);
            Assert.Equal(2, sent.AssignmentVersion);
            Assert.Equal(7, sent.ExpectedVersion);
        }
    }

    private static readonly Guid CaseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FollowUpId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EscalationId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task GetAsync_ProjectsFollowUpsAndEscalationsFromMonitoringQueue()
    {
        var client = CreateClient();
        var result = await client.GetAsync(CaseId, new WorkManagementQuery(new PageRequest(1, 25)));

        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.NotNull(result.Value);
        Assert.Equal(CaseId, result.Value.CaseId);
        Assert.Equal(7L, result.Value.Version);
        Assert.Equal(2, result.Value.Items.TotalCount);

        var followUp = Assert.Single(result.Value.Items.Items, item => item.Kind == WorkManagementKind.FollowUp);
        Assert.Equal(WorkManagementStatus.Scheduled, followUp.Status);
        Assert.Equal($"fu:{FollowUpId:D}", followUp.WorkKey);
        Assert.Contains(WorkManagementAction.RecordFollowUpOutcome, followUp.AvailableActions);
        Assert.Contains(WorkManagementAction.RescheduleFollowUp, followUp.AvailableActions);
        Assert.Contains(WorkManagementAction.CancelFollowUp, followUp.AvailableActions);

        var escalation = Assert.Single(result.Value.Items.Items, item => item.Kind == WorkManagementKind.Escalation);
        Assert.Equal(WorkManagementStatus.Requested, escalation.Status);
        Assert.Equal($"esc:{EscalationId:D}", escalation.WorkKey);
        Assert.Contains(WorkManagementAction.AcknowledgeEscalation, escalation.AvailableActions);
    }

    [Fact]
    public async Task GetAsync_DeniesWhenTimelineIsNotAuthorized()
    {
        var timeline = new StubTimelineClient(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        var client = new CommandBackedWorkManagementClient(
            new StubCommandClient(), new StubMonitoringClient(), timeline);

        var result = await client.GetAsync(CaseId, new WorkManagementQuery(new PageRequest(1, 25)));

        Assert.Equal(UiCapabilityResultCode.Denied, result.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task GetAsync_RejectsInvalidQuery()
    {
        var client = CreateClient();
        var result = await client.GetAsync(CaseId, new WorkManagementQuery(new PageRequest(0, 25)));

        Assert.Equal(UiCapabilityResultCode.InvalidRequest, result.Code);
    }

    [Fact]
    public async Task ExecuteAsync_ScheduleFollowUp_SendsCommandWithOwnerDefault()
    {
        var commands = new StubCommandClient();
        var client = new CommandBackedWorkManagementClient(
            commands, new StubMonitoringClient(), new StubTimelineClient());
        var command = new WorkManagementCommand(
            CaseId,
            "new",
            WorkManagementAction.ScheduleFollowUp,
            ExpectedVersion: 7,
            DueAtUtc: new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero),
            LocalTimeZoneId: "UTC",
            LocalOffset: TimeSpan.Zero);

        var result = await client.ExecuteAsync(command, new WorkManagementQuery(new PageRequest(1, 25)));

        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        var sent = Assert.Single(commands.Sent);
        Assert.Equal("ScheduleFollowUp", sent.Input.CommandType);
        Assert.Equal("owner-actor-1", sent.Input.AssigneeActorId);
        Assert.Equal(CaseId, sent.CaseId);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedAction_StaysFailClosedWithoutSending()
    {
        var commands = new StubCommandClient();
        var client = new CommandBackedWorkManagementClient(
            commands, new StubMonitoringClient(), new StubTimelineClient());
        var command = new WorkManagementCommand(
            CaseId,
            $"fu:{FollowUpId:D}",
            WorkManagementAction.AssignWorker,
            ExpectedVersion: 7,
            WorkerKey: "some-worker");

        var result = await client.ExecuteAsync(command, new WorkManagementQuery(new PageRequest(1, 25)));

        Assert.Equal(UiCapabilityResultCode.Unavailable, result.Code);
        Assert.Empty(commands.Sent);
    }

    [Fact]
    public async Task ExecuteAsync_MapsServerConflict()
    {
        var commands = new StubCommandClient
        {
            NextStatus = CaseCommandStatus.Conflict,
        };
        var client = new CommandBackedWorkManagementClient(
            commands, new StubMonitoringClient(), new StubTimelineClient());
        var command = new WorkManagementCommand(
            CaseId,
            $"esc:{EscalationId:D}",
            WorkManagementAction.AcknowledgeEscalation,
            ExpectedVersion: 7);

        var result = await client.ExecuteAsync(command, new WorkManagementQuery(new PageRequest(1, 25)));

        Assert.Equal(UiCapabilityResultCode.Conflict, result.Code);
    }

    private static CommandBackedWorkManagementClient CreateClient() =>
        new(new StubCommandClient(), new StubMonitoringClient(), new StubTimelineClient());

    private static CaseTimelineHeader Header() => new(
        CaseId,
        "org-1",
        "workspace-1",
        "ZW",
        "Active",
        "owner-actor-1",
        AggregateVersion: 7,
        ReportRevision: 3,
        new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));

    private sealed class StubTimelineClient(
        CaseTimelineAccess access = CaseTimelineAccess.Found,
        CaseTimeline? timeline = null) : ICaseTimelineClient
    {
        private readonly CaseTimeline _timeline = timeline ??
            new CaseTimeline(Header(), []);

        public Task<CaseTimelineResult> GetAsync(Guid caseId, int? limit, CancellationToken cancellationToken = default, long? beforeAggregateVersion = null) =>
            Task.FromResult(new CaseTimelineResult(access, access == CaseTimelineAccess.Found ? _timeline : null));
    }

    [Fact]
    public async Task Mixed_work_does_not_display_items_from_a_denied_queue_response()
    {
        var client = new CommandBackedWorkManagementClient(new StubCommandClient(),
            new StubMonitoringClient(denyAssigned: true), new StubTimelineClient());
        var result = await client.GetAsync(CaseId, new WorkManagementQuery(new PageRequest(1, 25)));
        Assert.Equal(UiCapabilityResultCode.Success, result.Code);
        Assert.Equal(WorkManagementKind.Escalation, Assert.Single(result.Value!.Items.Items).Kind);
    }

    private sealed class StubMonitoringClient(bool denyAssigned = false) : IMonitoringQueueClient
    {
        public Task<MonitoringQueueResult> GetAssignedAsync(int? limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MonitoringQueueResult(denyAssigned ? CaseQueueAccess.Denied : CaseQueueAccess.Allowed, [FollowUpItem()]));

        public Task<MonitoringQueueResult> GetOpenAsync(int? limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MonitoringQueueResult(CaseQueueAccess.Allowed, [EscalationItem()]));

        private static MonitoringQueueItem FollowUpItem() => new(
            CaseId,
            MonitoringQueueKind.FollowUp,
            FollowUpId,
            "Water point visit",
            "worker-actor-9",
            new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero),
            "Scheduled",
            IsOverdue: false,
            new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero),
            AggregateVersion: 7);

        private static MonitoringQueueItem EscalationItem() => new(
            CaseId,
            MonitoringQueueKind.Escalation,
            EscalationId,
            "Clinic stock shortage",
            AssigneeActorId: null,
            DueAtUtc: null,
            "Raised",
            IsOverdue: false,
            new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero),
            AggregateVersion: 7);
    }

    private sealed class StubCommandClient : ICaseCommandClient
    {
        public List<(Guid CaseId, CaseCommandInput Input)> Sent { get; } = [];
        public CaseCommandStatus NextStatus { get; set; } = CaseCommandStatus.Accepted;

        public Task<CaseCommandResult> SendAsync(Guid caseId, CaseCommandInput input, CancellationToken cancellationToken = default)
        {
            Sent.Add((caseId, input));
            return Task.FromResult(new CaseCommandResult(NextStatus, Guid.NewGuid(), [], 8, 3));
        }
    }
}
