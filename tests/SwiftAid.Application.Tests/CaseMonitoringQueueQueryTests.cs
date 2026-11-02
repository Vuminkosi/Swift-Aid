using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests;

/// <summary>Monitoring queue query: per-case ReadCase enforcement, skip-not-list, overdue derivation.</summary>
public sealed class CaseMonitoringQueueQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PastDue = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FutureDue = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Assigned_SkipsDeniedAndMissingCases_AndFlagsOverdue()
    {
        var readable = Guid.NewGuid();
        var denied = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var store = new StubQueueStore(
            assigned:
            [
                // The store contract returns due-ordered rows; the query preserves that order.
                Candidate(readable, "FollowUp", PastDue, nameof(FollowUpStatus.Scheduled)),
                Candidate(readable, "FollowUp", FutureDue, nameof(FollowUpStatus.Scheduled)),
                Candidate(denied, "FollowUp", FutureDue, nameof(FollowUpStatus.Scheduled)),
                Candidate(missing, "FollowUp", FutureDue, nameof(FollowUpStatus.Scheduled))
            ],
            open: [],
            raised: []);
        var query = new CaseMonitoringQueueQuery(
            store, new ScopedPolicy(denied), new ThrowingUnitOfWork(missing));

        var result = await query.GetAssignedAsync(CreateOfficer(), null, Now, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal(2, result.Items.Count);
        // Past-due first: the store returns due-ordered rows and the query preserves that order.
        Assert.Equal(PastDue, result.Items[0].DueAtUtc);
        Assert.True(result.Items[0].IsOverdue);
        Assert.Equal(FutureDue, result.Items[1].DueAtUtc);
        Assert.False(result.Items[1].IsOverdue);
    }

    [Fact]
    public async Task Open_CombinesFollowUpsAndRaisedEscalations_EscalationsNeverOverdue()
    {
        var caseId = Guid.NewGuid();
        var store = new StubQueueStore(
            assigned: [],
            open: [Candidate(caseId, "FollowUp", FutureDue, nameof(FollowUpStatus.Scheduled))],
            raised: [Candidate(caseId, "Escalation", null, nameof(EscalationStatus.Raised), "Escalation raised")]);
        var query = new CaseMonitoringQueueQuery(store, new AllowPolicy(), new StubUnitOfWork());

        var result = await query.GetOpenAsync(CreateOfficer(), null, Now, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal(2, result.Items.Count);
        var escalation = Assert.Single(result.Items, item => item.Kind == MonitoringQueueKind.Escalation);
        Assert.False(escalation.IsOverdue);
        Assert.Null(escalation.DueAtUtc);
        Assert.Equal("Escalation raised", escalation.Summary);
    }

    [Fact]
    public async Task MalformedCandidates_AreSkippedWithoutLease()
    {
        var store = new StubQueueStore(
            assigned:
            [
                new MonitoringQueueCandidate(Guid.NewGuid(), "NotAKind", Guid.NewGuid(), "x", null, null, "Scheduled", Now, 3),
                new MonitoringQueueCandidate(Guid.Empty, "FollowUp", Guid.NewGuid(), "x", null, FutureDue, "Scheduled", Now, 3),
                new MonitoringQueueCandidate(Guid.NewGuid(), "FollowUp", Guid.Empty, "x", null, FutureDue, "Scheduled", Now, 3)
            ],
            open: [],
            raised: []);
        var unitOfWork = new StubUnitOfWork();
        var query = new CaseMonitoringQueueQuery(store, new AllowPolicy(), unitOfWork);

        var result = await query.GetAssignedAsync(CreateOfficer(), null, Now, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Empty(result.Items);
        Assert.Equal(0, unitOfWork.BeginCalls);
    }

    [Fact]
    public async Task StoreFailure_ReturnsUnavailable()
    {
        var query = new CaseMonitoringQueueQuery(
            new ThrowingStore(), new AllowPolicy(), new StubUnitOfWork());

        var assigned = await query.GetAssignedAsync(CreateOfficer(), null, Now, CancellationToken.None);
        var open = await query.GetOpenAsync(CreateOfficer(), null, Now, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Unavailable, assigned.Access);
        Assert.Equal(CaseQueueAccess.Unavailable, open.Access);
    }

    [Fact]
    public async Task InvalidLimit_IsRejected()
    {
        var query = new CaseMonitoringQueueQuery(
            new StubQueueStore([], [], []), new AllowPolicy(), new StubUnitOfWork());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            query.GetAssignedAsync(CreateOfficer(), 0, Now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            query.GetOpenAsync(CreateOfficer(), 501, Now, CancellationToken.None));
    }

    private static MonitoringQueueCandidate Candidate(
        Guid caseId, string kind, DateTimeOffset? due, string status, string? summary = null) =>
        new(caseId, kind, Guid.NewGuid(), summary ?? "revisit holding", "worker-1",
            due, status, Now.AddDays(-1), 3);

    private static ActorContext CreateOfficer() => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "officer-1"),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "unit-test")));

    private sealed class AllowPolicy : ICaseAccessPolicy
    {
        public ValueTask<AccessDecision> AuthorizeAsync(
            ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(true, AccessDecisionCode.Allowed, null));

        public ValueTask<AccessDecision> AuthorizeAllAsync(
            ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> requiredOperations,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(true, AccessDecisionCode.Allowed, null));
    }

    private sealed class ScopedPolicy(Guid deniedCaseId) : ICaseAccessPolicy
    {
        public ValueTask<AccessDecision> AuthorizeAsync(
            ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(caseId == deniedCaseId
                ? new AccessDecision(false, AccessDecisionCode.GrantMissing, null)
                : new AccessDecision(true, AccessDecisionCode.Allowed, null));

        public ValueTask<AccessDecision> AuthorizeAllAsync(
            ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> requiredOperations,
            CancellationToken cancellationToken) =>
            AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, cancellationToken);
    }

    private sealed class StubLease : ICaseCommandLease
    {
        public Guid CaseId => Guid.Empty;
        public CaseLeaseMode Mode => CaseLeaseMode.Read;
        public ImmutableArray<string> LockedActorIds => [];
        public bool IsActive => true;
        public ValueTask CommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubUnitOfWork : ICaseCommandUnitOfWork
    {
        public int BeginCalls { get; private set; }

        public ValueTask<ICaseCommandLease> BeginCaseAsync(
            ActorContext actor,
            Guid caseId,
            ImmutableArray<string> additionalActorHints,
            CaseLeaseMode mode,
            CancellationToken cancellationToken)
        {
            BeginCalls++;
            return ValueTask.FromResult<ICaseCommandLease>(new StubLease());
        }
    }

    private sealed class ThrowingUnitOfWork(Guid missingCaseId) : ICaseCommandUnitOfWork
    {
        public ValueTask<ICaseCommandLease> BeginCaseAsync(
            ActorContext actor,
            Guid caseId,
            ImmutableArray<string> additionalActorHints,
            CaseLeaseMode mode,
            CancellationToken cancellationToken) =>
            caseId == missingCaseId
                ? throw new KeyNotFoundException("The case does not exist.")
                : ValueTask.FromResult<ICaseCommandLease>(new StubLease());
    }

    private sealed class StubQueueStore(
        IReadOnlyList<MonitoringQueueCandidate> assigned,
        IReadOnlyList<MonitoringQueueCandidate> open,
        IReadOnlyList<MonitoringQueueCandidate> raised) : ICaseMonitoringQueueStore
    {
        public Task<IReadOnlyList<MonitoringQueueCandidate>> GetAssignedFollowUpsAsync(
            string assigneeActorId, int take, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MonitoringQueueCandidate>>(assigned.Take(take).ToArray());

        public Task<IReadOnlyList<MonitoringQueueCandidate>> GetOpenFollowUpsAsync(
            int take, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MonitoringQueueCandidate>>(open.Take(take).ToArray());

        public Task<IReadOnlyList<MonitoringQueueCandidate>> GetRaisedEscalationsAsync(
            int take, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MonitoringQueueCandidate>>(raised.Take(take).ToArray());

        public Task<IReadOnlyList<MonitoringQueueCandidate>> GetAssignedAssistanceAsync(
            string actorId, int take, CancellationToken ct) => Task.FromResult<IReadOnlyList<MonitoringQueueCandidate>>([]);

        public Task<IReadOnlyList<MonitoringQueueCandidate>> GetOpenAssistanceAsync(
            int take, CancellationToken ct) => Task.FromResult<IReadOnlyList<MonitoringQueueCandidate>>([]);
    }

    private sealed class ThrowingStore : ICaseMonitoringQueueStore
    {
        public Task<IReadOnlyList<MonitoringQueueCandidate>> GetAssignedFollowUpsAsync(
            string assigneeActorId, int take, CancellationToken ct) =>
            throw new InvalidOperationException("The queue store is unavailable.");

        public Task<IReadOnlyList<MonitoringQueueCandidate>> GetOpenFollowUpsAsync(
            int take, CancellationToken ct) =>
            throw new InvalidOperationException("The queue store is unavailable.");

        public Task<IReadOnlyList<MonitoringQueueCandidate>> GetRaisedEscalationsAsync(
            int take, CancellationToken ct) =>
            throw new InvalidOperationException("The queue store is unavailable.");

        public Task<IReadOnlyList<MonitoringQueueCandidate>> GetAssignedAssistanceAsync(
            string actorId, int take, CancellationToken ct) =>
            throw new InvalidOperationException("The queue store is unavailable.");

        public Task<IReadOnlyList<MonitoringQueueCandidate>> GetOpenAssistanceAsync(
            int take, CancellationToken ct) =>
            throw new InvalidOperationException("The queue store is unavailable.");
    }
}
