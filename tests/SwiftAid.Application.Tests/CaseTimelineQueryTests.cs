using System.Collections.Immutable;
using System.Security.Claims;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Tests;

public sealed class CaseTimelineQueryTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task OfficerEscalationDetailsRequireBothManagementPermissions(bool escalation, bool followUp)
    {
        var header = new CaseTimelineHeader(Guid.NewGuid(), "org", "workspace", "SZ", "Active", "officer-1", 4, 1, DateTimeOffset.UtcNow);
        var row = Row(4, "CaseFollowUpScheduled");
        var store = new StubStore(header, null, [row])
        {
            OfficerAssignments = [new(row.EventId, Guid.NewGuid(), Guid.NewGuid(), "opaque-officer", "unsafe\nname", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), true, false)]
        };
        var permitted = escalation && followUp;
        var result = await new CaseTimelineQuery(store, new OfficerVisibilityPolicy(escalation, followUp), new StubUnitOfWork())
            .GetAsync(CreateOfficer(), header.CaseId, 1, CancellationToken.None);
        Assert.Equal(permitted, store.OfficerAssignmentsRead);
        var assignment = Assert.Single(result.Timeline!.Entries).OfficerEscalation;
        if (permitted) { Assert.NotNull(assignment); Assert.Null(assignment.OfficerDisplayLabel); }
        else Assert.Null(assignment);
    }

    private sealed class OfficerVisibilityPolicy(bool escalation, bool followUp) : ICaseAccessPolicy
    {
        public ValueTask<AccessDecision> AuthorizeAsync(ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken ct) =>
            ValueTask.FromResult(new AccessDecision(operation == CaseOperation.ReadCase, AccessDecisionCode.Allowed, null));
        public ValueTask<AccessDecision> AuthorizeAllAsync(ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> operations, CancellationToken ct)
        {
            var allowed = operations.Contains(CaseOperation.ManageEscalation) && operations.Contains(CaseOperation.ManageFollowUp) && escalation && followUp;
            return ValueTask.FromResult(new AccessDecision(allowed, allowed ? AccessDecisionCode.Allowed : AccessDecisionCode.GrantMissing, null));
        }
    }

    [Theory]
    [InlineData(false, "Active", false)]
    [InlineData(true, "Active", true)]
    [InlineData(true, "Resolved", false)]
    public async Task Collaborator_management_view_requires_permission_and_disables_new_grants_after_resolution(
        bool permitted, string workflow, bool canGrant)
    {
        var header = new CaseTimelineHeader(Guid.NewGuid(), "org", "workspace", "SZ", workflow, "officer-1", 4, 1, DateTimeOffset.UtcNow);
        var grant = new CaseCollaboratorGrantView(Guid.NewGuid(), "opaque-recipient", "Synthetic recipient",
            ["ReadCase"], DateTimeOffset.UtcNow.AddHours(1), false);
        var store = new StubStore(header, null, [Row(3, "CaseCollaboratorGranted"), Row(4, "CaseCollaboratorRevoked")])
        {
            Collaborators = [grant]
        };
        var query = new CaseTimelineQuery(store, new AssignmentPolicy(permitted), new StubUnitOfWork());
        var result = await query.GetAsync(CreateOfficer(), header.CaseId, 1, CancellationToken.None);
        Assert.Equal(permitted, store.CollaboratorsRead);
        if (permitted)
        {
            Assert.Equal(canGrant, result.Timeline!.Collaboration!.CanGrant);
            Assert.Equal(grant, Assert.Single(result.Timeline.Collaboration.Grants));
        }
        else
        {
            Assert.Null(result.Timeline!.Collaboration);
        }
        Assert.Equal("Collaborator access revoked", Assert.Single(result.Timeline.Entries).Label);
        Assert.Null((grant with { ActorDisplayLabel = null }).SafeActorDisplayLabel);
        Assert.Null((grant with { ActorDisplayLabel = "unsafe\nlabel" }).SafeActorDisplayLabel);
    }

    [Theory]
    [InlineData("officer-1", "Active", false, true, true)]
    [InlineData("officer-1", "Active", false, false, false)]
    [InlineData("other-owner", "Active", false, true, false)]
    [InlineData("officer-1", "Resolved", false, true, false)]
    [InlineData("officer-1", "Active", true, true, false)]
    public async Task Assignment_action_requires_pending_owner_and_explicit_permission(
        string owner, string workflow, bool acknowledged, bool granted, bool expected)
    {
        var header = new CaseTimelineHeader(Guid.NewGuid(), "org-1", "workspace-1", "ZW", workflow, owner, 3, 1,
            DateTimeOffset.UtcNow, 2, acknowledged ? DateTimeOffset.UtcNow : null, CanAcknowledgeAssignment: true);
        var query = new CaseTimelineQuery(new StubStore(header, null, []), new AssignmentPolicy(granted), new StubUnitOfWork());
        var result = await query.GetAsync(CreateOfficer(), header.CaseId, 1, CancellationToken.None);
        Assert.Equal(expected, result.Timeline!.Header.CanAcknowledgeAssignment);
    }

    private sealed class AssignmentPolicy(bool granted) : ICaseAccessPolicy
    {
        public ValueTask<AccessDecision> AuthorizeAsync(ActorContext actor, Guid caseId, CaseOperation operation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(operation == CaseOperation.ReadCase || granted,
                operation == CaseOperation.ReadCase || granted ? AccessDecisionCode.Allowed : AccessDecisionCode.GrantMissing, null));

        public ValueTask<AccessDecision> AuthorizeAllAsync(ActorContext actor, Guid caseId, IReadOnlyCollection<CaseOperation> operations, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessDecision(granted, granted ? AccessDecisionCode.Allowed : AccessDecisionCode.GrantMissing, null));
    }

    [Fact]
    public async Task Assignment_acknowledgement_has_a_readable_authoritative_label()
    {
        var header = new CaseTimelineHeader(Guid.NewGuid(), "org-zw-1", "workspace-1", "ZW", "Active", "officer-1", 3, 1,
            DateTimeOffset.UtcNow, AssignmentVersion: 2, AssignmentAcknowledgedAtUtc: DateTimeOffset.UtcNow);
        var query = new CaseTimelineQuery(new StubStore(header, null, [Row(3, "CaseAssignmentAcknowledged")]),
            new AllowPolicy(), new StubUnitOfWork());
        var result = await query.GetAsync(CreateOfficer(), header.CaseId, 1, CancellationToken.None);
        Assert.Equal(header, result.Timeline!.Header);
        Assert.Equal("Assignment acknowledged", Assert.Single(result.Timeline.Entries).Label);
    }

    [Theory]
    [InlineData("AssistanceRequested", "Assistance requested")]
    [InlineData("AssistanceAccepted", "Assistance responsibility accepted")]
    [InlineData("AssistanceProgressRecorded", "Assistance progress recorded")]
    [InlineData("AssistanceProvided", "Assistance provided; receipt unconfirmed")]
    [InlineData("AssistanceReceiptConfirmed", "Assistance receipt confirmed")]
    [InlineData("AssistanceUnfulfilled", "Assistance not fulfilled")]
    [InlineData("AssistanceCancelled", "Assistance request cancelled")]
    [InlineData("CaseFollowUpAcknowledged", "Follow-up acknowledged")]
    public async Task AssistanceLifecycleEvents_HaveExplicitSafeTimelineLabels(string eventType, string expectedLabel)
    {
        var header = new CaseTimelineHeader(Guid.NewGuid(), "org-1", "workspace-1", "SZ", "Active", "officer-1", 2, 1,
            DateTimeOffset.UtcNow);
        var query = new CaseTimelineQuery(new StubStore(header, null, [Row(2, eventType)]),
            new AllowPolicy(), new StubUnitOfWork());

        var result = await query.GetAsync(CreateOfficer(), header.CaseId, 1, CancellationToken.None);

        Assert.Equal(expectedLabel, Assert.Single(result.Timeline!.Entries).Label);
    }

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
    public async Task CursorPages_ReturnNewestFirstThenContinueWithOlderEntries()
    {
        var header = new CaseTimelineHeader(
            Guid.NewGuid(), "org-zw-1", "workspace-1", "ZW", "Active", "officer-1", 4, 1,
            new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        var store = new StubStore(header, null, [Row(1, "CaseReported"), Row(2, "CaseAssigned"), Row(3, "CaseResolved"), Row(4, "CaseReopened")]);
        var query = new CaseTimelineQuery(store, new AllowPolicy(), new StubUnitOfWork());

        var newestPage = await query.GetAsync(CreateOfficer(), header.CaseId, 2, CancellationToken.None);
        var olderCursor = Assert.IsType<long>(newestPage.Timeline!.OlderThanAggregateVersion);
        var olderPage = await query.GetAsync(CreateOfficer(), header.CaseId, 2, CancellationToken.None, olderCursor);

        Assert.Equal([3L, 4L], newestPage.Timeline.Entries.Select(entry => entry.AggregateVersion));
        Assert.True(newestPage.Timeline.HasOlderEntries);
        Assert.Equal(3, olderCursor);
        Assert.Equal([1L, 2L], olderPage.Timeline!.Entries.Select(entry => entry.AggregateVersion));
        Assert.False(olderPage.Timeline.HasOlderEntries);
        Assert.Null(olderPage.Timeline.OlderThanAggregateVersion);
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidCursor_ThrowsBeforeOpeningCaseLease(long cursor)
    {
        var unitOfWork = new StubUnitOfWork();
        var query = new CaseTimelineQuery(
            new StubStore(null, null, []), new AllowPolicy(), unitOfWork);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => query.GetAsync(CreateOfficer(), Guid.NewGuid(), null, CancellationToken.None, cursor));

        Assert.Equal(0, unitOfWork.BeginCalls);
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
        public bool CollaboratorsRead { get; private set; }
        public IReadOnlyList<CaseCollaboratorGrantView> Collaborators { get; set; } = [];
        public bool OfficerAssignmentsRead { get; private set; }
        public IReadOnlyList<CaseOfficerEscalationRow> OfficerAssignments { get; set; } = [];

        public Task<IReadOnlyList<CaseOfficerEscalationRow>> GetOfficerEscalationsAsync(ICaseCommandLease lease, Guid caseId, IReadOnlyList<Guid> eventIds, CancellationToken ct)
        {
            OfficerAssignmentsRead = true;
            return Task.FromResult<IReadOnlyList<CaseOfficerEscalationRow>>(OfficerAssignments.Where(item => eventIds.Contains(item.EventId)).ToArray());
        }

        public Task<IReadOnlyList<CaseCollaboratorGrantView>> GetCollaboratorsAsync(Guid caseId, CancellationToken ct)
        {
            CollaboratorsRead = true;
            return Task.FromResult(Collaborators);
        }

        public Task<CaseTimelineHeader?> GetHeaderAsync(Guid caseId, CancellationToken ct)
        {
            HeaderRead = true;
            return Task.FromResult(header);
        }

        public Task<IReadOnlyList<CaseTimelineRow>> GetEventsAsync(
            Guid caseId, int limit, long? beforeAggregateVersion, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CaseTimelineRow>>(rows
                .Where(row => beforeAggregateVersion is null || row.AggregateVersion < beforeAggregateVersion.Value)
                .OrderByDescending(row => row.AggregateVersion)
                .Take(limit)
                .ToArray());

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
