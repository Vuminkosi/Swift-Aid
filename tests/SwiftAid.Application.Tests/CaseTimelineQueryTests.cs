using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Tests;

public sealed class CaseTimelineQueryTests
{
    [Fact]
    public async Task Allowed_ReturnsOrderedEntriesWithDerivedPromotionFirst()
    {
        var header = new CaseTimelineHeader(
            Guid.NewGuid(), "org-zw-1", "workspace-1", "ZW", "Active", "officer-1", 3, 1,
            new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        var receiptId = Guid.NewGuid();
        var store = new StubStore(header, receiptId, [
            Row(3, "CaseResolved"),
            Row(1, "CaseReported"),
            Row(2, "CaseAssigned"),
        ]);
        var query = new CaseTimelineQuery(store, new AllowPolicy(), new StubUnitOfWork());

        var result = await query.GetAsync(CreateOfficer(), header.CaseId, null, CancellationToken.None);

        Assert.Equal(CaseTimelineAccess.Found, result.Access);
        Assert.NotNull(result.Timeline);
        var timeline = result.Timeline!;
        Assert.Equal(header, timeline.Header);
        Assert.Equal(4, timeline.Entries.Count);
        var derived = timeline.Entries[0];
        Assert.Equal(CaseTimelineEntryKind.Derived, derived.Kind);
        Assert.Equal("IntakePromoted", derived.EventType);
        Assert.Contains(receiptId.ToString("D"), derived.Label);
        Assert.Equal(
            ["Report accepted", "Accountable owner assigned", "Case resolved"],
            timeline.Entries.Skip(1).Select(entry => entry.Label));
        Assert.Equal([1, 2, 3], timeline.Entries.Skip(1).Select(entry => entry.AggregateVersion));
        Assert.All(timeline.Entries.Skip(1), entry =>
            Assert.Equal(CaseTimelineEntryKind.Authoritative, entry.Kind));
    }

    [Fact]
    public async Task Denied_ReturnsNotFoundWithoutReadingStore()
    {
        var store = new StubStore(null, null, []);
        var query = new CaseTimelineQuery(store, new DenyPolicy(), new StubUnitOfWork());

        var result = await query.GetAsync(CreateOfficer(), Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(CaseTimelineAccess.NotFoundOrNotAuthorized, result.Access);
        Assert.Null(result.Timeline);
        Assert.False(store.HeaderRead);
    }

    [Fact]
    public async Task MissingHeader_ReturnsNotFound()
    {
        var query = new CaseTimelineQuery(
            new StubStore(null, null, []), new AllowPolicy(), new StubUnitOfWork());

        var result = await query.GetAsync(CreateOfficer(), Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(CaseTimelineAccess.NotFoundOrNotAuthorized, result.Access);
    }

    [Fact]
    public async Task EmptyCaseId_ReturnsNotFoundWithoutLease()
    {
        var units = new StubUnitOfWork();
        var query = new CaseTimelineQuery(
            new StubStore(null, null, []), new AllowPolicy(), units);

        var result = await query.GetAsync(CreateOfficer(), Guid.Empty, null, CancellationToken.None);

        Assert.Equal(CaseTimelineAccess.NotFoundOrNotAuthorized, result.Access);
        Assert.Equal(0, units.BeginCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(501)]
    public async Task InvalidLimit_Throws(int limit)
    {
        var query = new CaseTimelineQuery(
            new StubStore(null, null, []), new AllowPolicy(), new StubUnitOfWork());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => query.GetAsync(CreateOfficer(), Guid.NewGuid(), limit, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownEventType_FallsBackToHonestLabel()
    {
        var header = new CaseTimelineHeader(
            Guid.NewGuid(), "org-zw-1", "workspace-1", "ZW", "Active", "officer-1", 1, 1,
            DateTimeOffset.UtcNow);
        var store = new StubStore(header, null, [Row(1, "CaseSomethingNew", schemaVersion: 2)]);
        var query = new CaseTimelineQuery(store, new AllowPolicy(), new StubUnitOfWork());

        var result = await query.GetAsync(CreateOfficer(), header.CaseId, null, CancellationToken.None);

        Assert.NotNull(result.Timeline);
        var entry = Assert.Single(result.Timeline!.Entries);
        Assert.Equal("Recorded event (CaseSomethingNew, schema v2)", entry.Label);
    }

    private static CaseTimelineRow Row(long version, string eventType, int schemaVersion = 1) => new(
        Guid.NewGuid(), eventType, version, 1, Guid.NewGuid(), "officer-1",
        null, DateTimeOffset.UtcNow, schemaVersion);

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

    private sealed class DenyPolicy : ICaseAccessPolicy
    {
        public ValueTask<AccessDecision> AuthorizeAsync(
            ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(false, AccessDecisionCode.GrantMissing, null));

        public ValueTask<AccessDecision> AuthorizeAllAsync(
            ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> requiredOperations,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(false, AccessDecisionCode.GrantMissing, null));
    }

    private sealed class StubStore(
        CaseTimelineHeader? header,
        Guid? receiptId,
        IReadOnlyList<CaseTimelineRow> rows) : ICaseTimelineStore
    {
        public bool HeaderRead { get; private set; }

        public Task<CaseTimelineHeader?> GetHeaderAsync(Guid caseId, CancellationToken ct)
        {
            HeaderRead = true;
            return Task.FromResult(header);
        }

        public Task<IReadOnlyList<CaseTimelineRow>> GetEventsAsync(Guid caseId, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CaseTimelineRow>>(rows.Take(limit).ToArray());

        public Task<Guid?> GetPromotingReceiptAsync(Guid caseId, CancellationToken ct) =>
            Task.FromResult(receiptId);
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
}
