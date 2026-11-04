using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Application.Tests;

public sealed class CaseWorkDiscoveryQueryTests
{
    [Fact]
    public async Task SkipsDeniedCandidatesAcrossBatchesAndCursorOnlyContainsReturnedCase()
    {
        var store = new Store();
        var policy = new Policy();
        var query = new CaseWorkDiscoveryQuery(store, policy, new UnitOfWork());
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "worker"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var first = await query.GetAsync(actor, CaseWorkDiscoveryKind.FollowUp, CaseWorkDiscoveryScope.Assigned, 1, null, default);
        Assert.Equal(CaseQueueAccess.Allowed, first.Access);
        Assert.Equal(Store.Id(201), Assert.Single(first.CaseIds));
        Assert.Equal(Store.Id(201), first.NextCursor);
        Assert.Equal("worker", store.Actor);
        policy.DenyLast = true;
        var second = await query.GetAsync(actor, CaseWorkDiscoveryKind.FollowUp, CaseWorkDiscoveryScope.Assigned, 1, first.NextCursor, default);
        Assert.Equal(CaseQueueAccess.Allowed, second.Access);
        Assert.Empty(second.CaseIds);
        Assert.Null(second.NextCursor);
        Assert.True(store.Reads >= 3);
    }

    private sealed class Store : ICaseWorkDiscoveryStore
    {
        public int Reads { get; private set; }
        public string? Actor { get; private set; }
        public static Guid Id(int value) => Guid.Parse($"{value:x8}-0000-0000-0000-000000000001");
        public Task<IReadOnlyList<Guid>> GetCasesAfterAsync(CaseWorkDiscoveryKind kind, string? assignedActorId,
            Guid? after, int take, CancellationToken cancellationToken)
        {
            Reads++;
            Actor = assignedActorId;
            return Task.FromResult<IReadOnlyList<Guid>>(Enumerable.Range(1, 202).Select(Id)
                .Where(id => after is null || id.CompareTo(after.Value) > 0).Take(take).ToArray());
        }
    }

    private sealed class Policy : ICaseAccessPolicy
    {
        public bool DenyLast { get; set; }
        public ValueTask<AccessDecision> AuthorizeAsync(ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken)
        {
            var allowed = caseId == Store.Id(201) || caseId == Store.Id(202) && !DenyLast;
            return ValueTask.FromResult(new AccessDecision(allowed, allowed ? AccessDecisionCode.Allowed : AccessDecisionCode.GrantMissing, null));
        }
        public ValueTask<AccessDecision> AuthorizeAllAsync(ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> operations, CancellationToken cancellationToken) =>
            AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, cancellationToken);
    }

    private sealed class UnitOfWork : ICaseCommandUnitOfWork
    {
        public ValueTask<ICaseCommandLease> BeginCaseAsync(ActorContext actor, Guid caseId, ImmutableArray<string> hints,
            CaseLeaseMode mode, CancellationToken cancellationToken) => ValueTask.FromResult<ICaseCommandLease>(new Lease(caseId));
        private sealed class Lease(Guid caseId) : ICaseCommandLease
        {
            public Guid CaseId => caseId;
            public CaseLeaseMode Mode => CaseLeaseMode.Read;
            public ImmutableArray<string> LockedActorIds => [];
            public bool IsActive => true;
            public ValueTask CommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
