using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Presentation;

/// <summary>Case clients that share one explicitly registered in-memory practice store.</summary>
public sealed class FakeCaseQueueClient(FakeCaseStore store) : ICaseQueueClient
{
    private readonly FakeCaseStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetMine(limit, cancellationToken));
}

public sealed class FakeCaseTimelineClient(FakeCaseStore store) : ICaseTimelineClient
{
    private readonly FakeCaseStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<CaseTimelineResult> GetAsync(Guid caseId, int? limit, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetTimeline(caseId, limit, cancellationToken));
}

public sealed class FakeMonitoringQueueClient(FakeCaseStore store) : IMonitoringQueueClient
{
    private readonly FakeCaseStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<MonitoringQueueResult> GetAssignedAsync(int? limit, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetMonitoring(assignedOnly: true, limit, cancellationToken));

    public Task<MonitoringQueueResult> GetOpenAsync(int? limit, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetMonitoring(assignedOnly: false, limit, cancellationToken));
}

public sealed class FakeCaseCommandClient(FakeCaseStore store) : ICaseCommandClient
{
    private readonly FakeCaseStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<CaseCommandResult> SendAsync(
        Guid caseId,
        CaseCommandInput input,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.SendCaseCommand(caseId, input, cancellationToken));
}

public sealed class FakeIntakeCoordinationClient(FakeCaseStore store) : IIntakeCoordinationClient
{
    private readonly FakeCaseStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<(CoordinationQueueAccess Access, IReadOnlyList<IntakeCoordinationQueueEntry> Entries)> GetQueueAsync(
        IntakeRoutingStatus? routingStatus,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetCoordinationQueue(routingStatus, limit, cancellationToken));

    public Task<IntakeCoordinationDetail?> GetDetailAsync(Guid receiptId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetCoordinationDetail(receiptId, cancellationToken));

    public Task<IReadOnlyList<IntakeCoordinationQueueOption>> GetQueuesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetCoordinationQueues(cancellationToken));

    public Task<CoordinationActionOutcome> ActAsync(
        Guid receiptId,
        long expectedVersion,
        IntakeCoordinationAction action,
        string? targetQueueId,
        string reason,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.ActOnCoordination(receiptId, expectedVersion, action, targetQueueId, reason, cancellationToken));
}

public sealed class FakeWorkManagementClient(FakeCaseStore store) : IWorkManagementClient
{
    private readonly FakeCaseStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<UiCapabilityResult<WorkManagementSnapshot>> GetAsync(
        Guid caseId,
        WorkManagementQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetWorkManagement(caseId, query, cancellationToken));

    public Task<UiCapabilityResult<WorkManagementSnapshot>> ExecuteAsync(
        WorkManagementCommand command,
        WorkManagementQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.ApplyWorkManagement(command, query, cancellationToken));
}
