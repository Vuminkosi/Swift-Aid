using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Ui.TestHost;

public sealed class AssignmentsFixture : ICaseQueueClient, ICaseWorkDiscoveryClient, IWorkManagementClient
{
    private static readonly Guid First = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    public bool Denied { get; set; }
    public Task<CaseWorkDiscoveryPage> GetAsync(CaseWorkDiscoveryKind kind, CaseWorkDiscoveryScope scope,
        int limit, Guid? cursor, CancellationToken cancellationToken = default) =>
        Task.FromResult(Denied ? new CaseWorkDiscoveryPage(CaseQueueAccess.Denied, []) :
            new CaseWorkDiscoveryPage(CaseQueueAccess.Allowed, [First, Second]));
    public Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default) => GetMineAsync(limit, null, cancellationToken);
    public Task<CaseQueueResult> GetMineAsync(int? limit, string? cursor, CancellationToken cancellationToken = default)
    {
        if (Denied) return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Denied, []));
        var id = cursor is null ? First : Second;
        var row = new CaseQueueItem(id, "Open", "SZ", "fixture", 1, DateTimeOffset.UtcNow);
        return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, [row], cursor is null ? "next" : null));
    }

    public Task<UiCapabilityResult<WorkManagementSnapshot>> GetAsync(Guid caseId, WorkManagementQuery query, CancellationToken cancellationToken = default)
    {
        var prefix = caseId == First ? "First" : "Second";
        var kind = query.Kind ?? WorkManagementKind.Assignment;
        var noun = kind switch { WorkManagementKind.Assistance => "assistance", WorkManagementKind.FollowUp => "follow-up",
            WorkManagementKind.Escalation => "escalation", _ => "assignment" };
        var items = Enumerable.Range(0, caseId == First ? 105 : 15).Select(index => new WorkManagementItem($"{caseId:N}-{index}", "Case fixture", kind,
            WorkManagementStatus.Assigned, WorkManagementAssignmentStatus.AssignedToYou,
            index == 0 ? $"{prefix} synthetic {noun}" : $"{prefix} {noun} {index}",
            DateTimeOffset.UnixEpoch.AddMinutes(100 - index * 2 - (caseId == First ? 0 : 1)), null, [], [], [],
            AssignedToCurrentActor: kind == WorkManagementKind.FollowUp && index != 1)).ToArray();
        var page = items.Skip((query.Page.PageNumber - 1) * query.Page.PageSize).Take(query.Page.PageSize).ToArray();
        var snapshot = new WorkManagementSnapshot(caseId, 1, new Page<WorkManagementItem>(page, query.Page.PageNumber, query.Page.PageSize,
            items.Length, query.Page.PageNumber * query.Page.PageSize < items.Length));
        return Task.FromResult(new UiCapabilityResult<WorkManagementSnapshot>(UiCapabilityResultCode.Success, snapshot,
            UiCapabilityResults.SyntheticMetadata("work", "Fixture"), kind == WorkManagementKind.Assistance
                ? CommandBackedWorkManagementClient.LimitedMonitoringNotice : null));
    }

    public Task<UiCapabilityResult<WorkManagementSnapshot>> ExecuteAsync(WorkManagementCommand command, WorkManagementQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(UiCapabilityResults.Unavailable<WorkManagementSnapshot>("fixture"));
}
