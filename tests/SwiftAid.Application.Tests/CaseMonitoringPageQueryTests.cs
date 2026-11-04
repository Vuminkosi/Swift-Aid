using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Application.Tests;

public sealed class CaseMonitoringPageQueryTests
{
    [Fact]
    public async Task ContinuationRechecksAccessAndVersionUnderReadLease()
    {
        var id = Guid.NewGuid();
        var store = new Store(id);
        var timeline = new Timeline(id);
        var policy = new Policy();
        var leases = new UnitOfWork();
        var query = new CaseMonitoringPageQuery(store, timeline, policy, leases, TimeProvider.System);
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "fixture"), new Claim(ActorContext.AccessEpochClaimType, "1")], "fixture")));

        var first = await query.GetAsync(actor, id, 0, 1, null, default);
        Assert.Equal(CaseMonitoringPageAccess.Found, first.Access);
        Assert.Equal(1, first.Page!.NextOffset);
        Assert.Single(first.Page.Items);
        var second = await query.GetAsync(actor, id, 1, 1, first.Page.AggregateVersion, default);
        Assert.Equal(CaseMonitoringPageAccess.Found, second.Access);
        Assert.Null(second.Page!.NextOffset);
        Assert.NotEqual(first.Page.Items[0].Work.WorkItemId, second.Page.Items[0].Work.WorkItemId);
        Assert.True(first.Page.Items[0].AssignedToCurrentActor);
        timeline.Version++;
        Assert.Equal(CaseMonitoringPageAccess.Stale, (await query.GetAsync(actor, id, 1, 1, 7, default)).Access);
        policy.Allowed = false;
        Assert.Equal(CaseMonitoringPageAccess.NotFoundOrNotAuthorized, (await query.GetAsync(actor, id, 0, 1, null, default)).Access);
        Assert.Equal(2, store.Reads);
        Assert.Equal(4, leases.Begins);
        Assert.Equal(2, leases.Commits);
    }

    private sealed class Store(Guid caseId) : ICaseMonitoringPageStore
    {
        public int Reads { get; private set; }
        public Task<IReadOnlyList<MonitoringQueueItem>> ReadAsync(ICaseCommandLease lease, int offset, int take, DateTimeOffset utcNow, CancellationToken cancellationToken)
        {
            Assert.True(lease.IsActive);
            Assert.Equal(CaseLeaseMode.Read, lease.Mode);
            Reads++;
            var rows = Enumerable.Range(0, 2).Select(index => new MonitoringQueueItem(caseId, MonitoringQueueKind.Assistance,
                Guid.Parse($"{index + 1:x8}-0000-0000-0000-000000000001"), "Fixture", "fixture", null, "Requested", false, utcNow, 7)).ToArray();
            return Task.FromResult<IReadOnlyList<MonitoringQueueItem>>(rows.Skip(offset).Take(take).ToArray());
        }
    }

    private sealed class Timeline(Guid caseId) : ICaseTimelineStore
    {
        public long Version { get; set; } = 7;
        public Task<CaseTimelineHeader?> GetHeaderAsync(Guid id, CancellationToken ct) => Task.FromResult<CaseTimelineHeader?>(
            new(caseId, "org", "workspace", "SZ", "Active", "fixture", Version, 1, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<CaseTimelineRow>> GetEventsAsync(Guid id, int limit, long? beforeAggregateVersion, CancellationToken ct) => throw new NotSupportedException();
        public Task<Guid?> GetPromotingReceiptAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CaseCollaboratorGrantView>> GetCollaboratorsAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Policy : ICaseAccessPolicy
    {
        public bool Allowed { get; set; } = true;
        public ValueTask<AccessDecision> AuthorizeAsync(ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(Allowed, Allowed ? AccessDecisionCode.Allowed : AccessDecisionCode.GrantMissing, null));
        public ValueTask<AccessDecision> AuthorizeAllAsync(ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> requiredOperations, CancellationToken cancellationToken) =>
            AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, cancellationToken);
    }

    private sealed class UnitOfWork : ICaseCommandUnitOfWork
    {
        public int Begins { get; private set; }
        public int Commits { get; private set; }
        public ValueTask<ICaseCommandLease> BeginCaseAsync(ActorContext actor, Guid caseId, ImmutableArray<string> additionalActorHints, CaseLeaseMode mode, CancellationToken ct)
        {
            Begins++;
            return ValueTask.FromResult<ICaseCommandLease>(new Lease(this, caseId));
        }
        private sealed class Lease(UnitOfWork owner, Guid caseId) : ICaseCommandLease
        {
            public Guid CaseId => caseId;
            public CaseLeaseMode Mode => CaseLeaseMode.Read;
            public ImmutableArray<string> LockedActorIds => [];
            public bool IsActive => true;
            public ValueTask CommitAsync(CancellationToken cancellationToken) { owner.Commits++; return ValueTask.CompletedTask; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
