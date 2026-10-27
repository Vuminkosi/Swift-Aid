using System.Text;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Client.LocalStore;
using SwiftAid.Client.Outbox;
using SwiftAid.Client.Sync;

namespace SwiftAid.Client.Tests;

public sealed class OfflineCommandTests
{
    [Fact]
    public async Task RetryAfterAmbiguousTimeout_ReusesCommandIdAndFrozenPayload()
    {
        await using var fixture = new OutboxFixture();
        var command = Proposal(caseId: Guid.NewGuid());
        await fixture.Outbox.EnqueueAsync(command, CancellationToken.None);

        var first = await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None);
        Assert.NotNull(first);
        var firstSend = await fixture.Outbox.FreezeForSendAsync(
            first.CommandId, first.BaseAggregateVersion, Payload("first-send"), fixture.CurrentAuthority, CancellationToken.None);
        await fixture.Outbox.ScheduleRetryAsync(firstSend.CommandId, CancellationToken.None);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var retry = await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None);
        Assert.NotNull(retry);
        var secondSend = await fixture.Outbox.FreezeForSendAsync(
            retry.CommandId, retry.BaseAggregateVersion + 99, Payload("changed-after-timeout"), fixture.CurrentAuthority, CancellationToken.None);

        Assert.Equal(firstSend.CommandId, secondSend.CommandId);
        Assert.Equal(firstSend.ExpectedAggregateVersion, secondSend.ExpectedAggregateVersion);
        Assert.Equal(firstSend.Payload, secondSend.Payload);
        Assert.Equal(firstSend.PayloadHash, secondSend.PayloadHash);
        Assert.Equal(2, secondSend.AttemptCount);
    }

    [Fact]
    public async Task TimeoutAfterServerCommit_RetryReturnsOneBusinessAcceptance()
    {
        await using var fixture = new OutboxFixture();
        var proposal = Proposal(Guid.NewGuid());
        await fixture.Outbox.EnqueueAsync(proposal, CancellationToken.None);
        var transport = new CommitThenTimeoutTransport();
        var coordinator = new SyncCoordinator(
            fixture.Outbox,
            new ReadyAccessRefresher(),
            new PayloadBinder(),
            transport,
            new StaticProjectionRefresher(new ProjectionRefreshResult(false, new HashSet<Guid>())));

        var first = await coordinator.SynchronizeAsync(SyncTrigger.Manual, CancellationToken.None);
        Assert.Equal(1, first.RetryScheduled);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        var second = await coordinator.SynchronizeAsync(SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(1, second.AcceptedAwaitingRefresh);
        Assert.Equal(1, transport.BusinessAcceptances);
        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal(transport.Sent[0].CommandId, transport.Sent[1].CommandId);
        Assert.Equal(transport.Sent[0].Payload, transport.Sent[1].Payload);
        Assert.Equal(transport.Sent[0].PayloadHash, transport.Sent[1].PayloadHash);
    }

    [Fact]
    public async Task DependentCommand_UsesAcknowledgedPriorAggregateVersion()
    {
        await using var fixture = new OutboxFixture();
        var caseId = Guid.NewGuid();
        var first = Proposal(caseId, baseVersion: 4);
        var second = Proposal(caseId, baseVersion: 4, dependencyId: first.CommandId);
        await fixture.Outbox.EnqueueAsync(first, CancellationToken.None);
        await fixture.Outbox.EnqueueAsync(second, CancellationToken.None);

        var firstReady = await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None);
        Assert.NotNull(firstReady);
        Assert.Equal(first.CommandId, firstReady.CommandId);
        var firstSend = await fixture.Outbox.FreezeForSendAsync(
            first.CommandId, firstReady.BaseAggregateVersion, Payload("first"), fixture.CurrentAuthority, CancellationToken.None);
        await fixture.Outbox.MarkAcknowledgedAsync(
            new LocalCommandReceipt(firstSend.CommandId, caseId, AggregateVersion: 5), CancellationToken.None);

        var secondReady = await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None);

        Assert.NotNull(secondReady);
        Assert.Equal(second.CommandId, secondReady.CommandId);
        Assert.Equal(5, secondReady.BaseAggregateVersion);
    }

    [Fact]
    public async Task Conflict_BlocksSameCaseDependentsButDoesNotBlockAnotherCase()
    {
        await using var fixture = new OutboxFixture();
        var caseId = Guid.NewGuid();
        var blocked = Proposal(caseId);
        var dependent = Proposal(caseId, dependencyId: blocked.CommandId);
        var unrelated = Proposal(Guid.NewGuid());
        await fixture.Outbox.EnqueueAsync(blocked, CancellationToken.None);
        await fixture.Outbox.EnqueueAsync(dependent, CancellationToken.None);
        await fixture.Outbox.EnqueueAsync(unrelated, CancellationToken.None);

        var first = await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None);
        Assert.NotNull(first);
        var sent = await fixture.Outbox.FreezeForSendAsync(
            first.CommandId, first.BaseAggregateVersion, Payload("proposal"), fixture.CurrentAuthority, CancellationToken.None);
        await fixture.Outbox.MarkConflictAsync(
            sent.CommandId, latestAggregateVersion: 8, Payload("authorized-latest"), CancellationToken.None);

        var next = await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None);

        Assert.NotNull(next);
        Assert.Equal(unrelated.CommandId, next.CommandId);
        Assert.NotEqual(dependent.CommandId, next.CommandId);
        var conflict = await fixture.Outbox.GetConflictAsync(blocked.CommandId, CancellationToken.None);
        Assert.NotNull(conflict);
        Assert.Equal(Payload("proposal"), conflict.ProposalPayload);
        Assert.Equal(Payload("authorized-latest"), conflict.LatestAuthorizedSnapshot);
        Assert.Equal(8, conflict.LatestAggregateVersion);
    }

    [Fact]
    public async Task ExpiredSendingLease_IsRecoverableAfterOutboxReopen()
    {
        await using var fixture = new OutboxFixture();
        var proposal = Proposal(Guid.NewGuid());
        await fixture.Outbox.EnqueueAsync(proposal, CancellationToken.None);
        var claimed = await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None);
        Assert.NotNull(claimed);
        var original = await fixture.Outbox.FreezeForSendAsync(
            claimed.CommandId, claimed.BaseAggregateVersion, Payload("frozen"), fixture.CurrentAuthority, CancellationToken.None);

        var reopened = fixture.CreateOutbox();
        Assert.Null(await reopened.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None));
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));

        var recovered = await reopened.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None);
        Assert.NotNull(recovered);
        var retry = await reopened.FreezeForSendAsync(
            recovered.CommandId, recovered.BaseAggregateVersion, Payload("must-not-replace"), fixture.CurrentAuthority, CancellationToken.None);

        Assert.Equal(original.CommandId, retry.CommandId);
        Assert.Equal(original.Payload, retry.Payload);
        Assert.Equal(original.PayloadHash, retry.PayloadHash);
        Assert.Equal(2, retry.AttemptCount);
    }

    [Fact]
    public async Task AccountSwitchCannotReadAnotherAccountsQueue_AndEpochChangeBlocksOldCommands()
    {
        await using var fixture = new OutboxFixture();
        var proposal = Proposal(Guid.NewGuid());
        await fixture.Outbox.EnqueueAsync(proposal, CancellationToken.None);

        fixture.Access.Context = new LocalDraftAccessContext(
            "actor-b", true, true, 1, fixture.Clock.GetUtcNow().AddHours(1), ["workspace-a"]);
        Assert.Empty(await fixture.Outbox.GetStatusesAsync(CancellationToken.None));
        Assert.Null(await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None));

        fixture.Access.Context = new LocalDraftAccessContext(
            "actor-a", true, true, 2, fixture.Clock.GetUtcNow().AddHours(1), ["workspace-a"]);
        Assert.Null(await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None));
        Assert.Empty(await fixture.Outbox.GetStatusesAsync(CancellationToken.None));
        await using (var db = fixture.CreateDbContext())
        {
            var retained = await db.Commands.SingleAsync();
            Assert.Equal(proposal.CommandId.ToString("D"), retained.CommandId);
            Assert.Equal(LocalCommandState.Queued, retained.State);
        }

        var currentEpochCommand = Proposal(proposal.CaseId);
        await fixture.Outbox.EnqueueAsync(currentEpochCommand, CancellationToken.None);
        var ready = await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None);

        Assert.NotNull(ready);
        Assert.Equal(currentEpochCommand.CommandId, ready.CommandId);
        var rows = await fixture.Outbox.GetStatusesAsync(CancellationToken.None);
        var currentStatus = Assert.Single(rows);
        Assert.Equal(currentEpochCommand.CommandId, currentStatus.CommandId);
        Assert.Equal(LocalCommandState.Sending, currentStatus.State);
    }

    [Fact]
    public async Task RevokedWorkspaceStatusesAreHiddenWithoutChangingRetainedCommand()
    {
        await using var fixture = new OutboxFixture();
        var proposal = Proposal(Guid.NewGuid());
        await fixture.Outbox.EnqueueAsync(proposal, CancellationToken.None);

        fixture.Access.Context = new LocalDraftAccessContext(
            "actor-a", true, true, 1, fixture.Clock.GetUtcNow().AddHours(1), []);

        Assert.Empty(await fixture.Outbox.GetStatusesAsync(CancellationToken.None));
        Assert.Empty(await fixture.Outbox.GetAcknowledgedCommandIdsForAuthorizedWorkspacesAsync(
            fixture.CurrentAuthority, CancellationToken.None));
        Assert.Null(await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None));
        await using var db = fixture.CreateDbContext();
        var retained = await db.Commands.SingleAsync();
        Assert.Equal(proposal.CommandId.ToString("D"), retained.CommandId);
        Assert.Equal(LocalCommandState.Queued, retained.State);
    }

    [Fact]
    public async Task AcceptedAwaitingRefresh_RemainsAcknowledgedAndHiddenAfterWorkspaceRevocation()
    {
        await using var fixture = new OutboxFixture();
        var proposal = Proposal(Guid.NewGuid());
        await fixture.Outbox.EnqueueAsync(proposal, CancellationToken.None);
        var transport = new RecordingTransport
        {
            Result = new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.Accepted, AcceptedAggregateVersion: 2)
        };
        var coordinator = new SyncCoordinator(
            fixture.Outbox,
            new ReadyAccessRefresher(),
            new PayloadBinder(),
            transport,
            new StaticProjectionRefresher(new ProjectionRefreshResult(true, new HashSet<Guid>())));

        var summary = await coordinator.SynchronizeAsync(SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(1, summary.AcceptedAwaitingRefresh);
        Assert.Equal(LocalCommandState.Acknowledged,
            Assert.Single(await fixture.Outbox.GetStatusesAsync(CancellationToken.None)).State);

        fixture.Access.Context = new LocalDraftAccessContext(
            "actor-a", true, true, 1, fixture.Clock.GetUtcNow().AddHours(1), []);

        Assert.Empty(await fixture.Outbox.GetStatusesAsync(CancellationToken.None));
        Assert.Null(await fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None));
        await using var db = fixture.CreateDbContext();
        var retained = await db.Commands.SingleAsync();
        Assert.Equal(LocalCommandState.Acknowledged, retained.State);
        Assert.Equal(2, retained.AcceptedAggregateVersion);
    }

    [Fact]
    public async Task AcceptedAwaitingRefresh_ReconcilesAfterEpochAdvanceWithoutResending()
    {
        await using var fixture = new OutboxFixture();
        var proposal = Proposal(Guid.NewGuid());
        await fixture.Outbox.EnqueueAsync(proposal, CancellationToken.None);
        var initialTransport = new RecordingTransport
        {
            Result = new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.Accepted, AcceptedAggregateVersion: 2)
        };
        var unavailableCoordinator = new SyncCoordinator(
            fixture.Outbox,
            new ReadyAccessRefresher(),
            new PayloadBinder(),
            initialTransport,
            new StaticProjectionRefresher(new ProjectionRefreshResult(false, new HashSet<Guid>())));

        var initialSummary = await unavailableCoordinator.SynchronizeAsync(SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(1, initialSummary.AcceptedAwaitingRefresh);
        Assert.Single(initialTransport.Sent);

        fixture.Access.Context = new LocalDraftAccessContext(
            "actor-a", true, true, 2, fixture.Clock.GetUtcNow().AddHours(1), ["workspace-a"]);
        var refreshedAuthority = fixture.CurrentAuthority;
        var retryTransport = new RecordingTransport();
        var reconciledCoordinator = new SyncCoordinator(
            fixture.Outbox,
            new FixedAccessRefresher(refreshedAuthority),
            new PayloadBinder(),
            retryTransport,
            new StaticProjectionRefresher(new ProjectionRefreshResult(true, new HashSet<Guid> { proposal.CommandId })));

        var reconciled = await reconciledCoordinator.SynchronizeAsync(SyncTrigger.Resume, CancellationToken.None);

        Assert.Equal(0, reconciled.Sent);
        Assert.Empty(retryTransport.Sent);
        Assert.Equal(1, reconciled.Synchronized);
        Assert.Empty(await fixture.Outbox.GetStatusesAsync(CancellationToken.None));
        await using var db = fixture.CreateDbContext();
        var retained = await db.Commands.SingleAsync();
        Assert.Equal(LocalCommandState.Synchronized, retained.State);
        Assert.Equal(2, retained.AcceptedAggregateVersion);
    }

    [Fact]
    public async Task PayloadIsEncryptedAndChangingStoredAccessEpochFailsIntegrityCheck()
    {
        await using var fixture = new OutboxFixture();
        var proposal = Proposal(Guid.NewGuid());
        await fixture.Outbox.EnqueueAsync(proposal, CancellationToken.None);

        var databaseText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.DatabasePath));
        Assert.DoesNotContain("proposal", databaseText, StringComparison.Ordinal);

        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.Commands.SingleAsync();
            row.AccessEpoch = 2;
            await db.SaveChangesAsync();
        }

        fixture.Access.Context = new LocalDraftAccessContext(
            "actor-a", true, true, 2, fixture.Clock.GetUtcNow().AddHours(1), ["workspace-a"]);

        await Assert.ThrowsAsync<LocalCommandIntegrityException>(
            () => fixture.Outbox.GetNextReadyAsync(fixture.CurrentAuthority, CancellationToken.None));
    }

    [Fact]
    public async Task AcceptedReceiptWithoutMatchingPull_ReportsAcceptedRefreshing()
    {
        await using var fixture = new OutboxFixture();
        var proposal = Proposal(Guid.NewGuid());
        await fixture.Outbox.EnqueueAsync(proposal, CancellationToken.None);
        var transport = new RecordingTransport
        {
            Result = new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.Accepted, AcceptedAggregateVersion: 2)
        };
        var pull = new StaticProjectionRefresher(new ProjectionRefreshResult(true, new HashSet<Guid>()));
        var coordinator = new SyncCoordinator(
            fixture.Outbox,
            new ReadyAccessRefresher(),
            new PayloadBinder(),
            transport,
            pull);

        var summary = await coordinator.SynchronizeAsync(SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(1, summary.Sent);
        Assert.Equal(1, summary.AcceptedAwaitingRefresh);
        Assert.Equal(0, summary.Synchronized);
        Assert.Single(transport.Sent);
        Assert.Equal(LocalCommandState.Acknowledged,
            Assert.Single(await fixture.Outbox.GetStatusesAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task StaleRefreshedActorEpochOrWorkspace_FailsClosedBeforeNetworkSend()
    {
        await using var fixture = new OutboxFixture();
        var proposal = Proposal(Guid.NewGuid());
        await fixture.Outbox.EnqueueAsync(proposal, CancellationToken.None);
        var transport = new RecordingTransport();
        var staleAuthorities = new[]
        {
            new SyncAccessResult(true, "actor-b", 1, new HashSet<string>(["workspace-a"], StringComparer.Ordinal)),
            new SyncAccessResult(true, "actor-a", 2, new HashSet<string>(["workspace-a"], StringComparer.Ordinal)),
            new SyncAccessResult(true, "actor-a", 1, new HashSet<string>(["workspace-b"], StringComparer.Ordinal))
        };

        foreach (var authority in staleAuthorities)
        {
            var coordinator = new SyncCoordinator(
                fixture.Outbox,
                new FixedAccessRefresher(authority),
                new PayloadBinder(),
                transport,
                new StaticProjectionRefresher(new ProjectionRefreshResult(false, new HashSet<Guid>())));

            var summary = await coordinator.SynchronizeAsync(SyncTrigger.Manual, CancellationToken.None);

            Assert.False(summary.AccessReady);
        }

        Assert.Empty(transport.Sent);
        var retained = Assert.Single(await fixture.Outbox.GetStatusesAsync(CancellationToken.None));
        Assert.Equal(proposal.CommandId, retained.CommandId);
        Assert.Equal(LocalCommandState.Queued, retained.State);
    }

    [Fact]
    public async Task ReconciledProposal_UsesNewCommandIdAndLatestVersion()
    {
        var conflict = new LocalCommandConflict(
            Guid.NewGuid(), Guid.NewGuid(), "workspace-a", 1, 3,
            Payload("old"), Payload("server"), 7);
        var nextId = Guid.NewGuid();

        var reconciled = ConflictResolver.CreateReconciledProposal(
            conflict, nextId, "CorrectReport", Payload("human-reconciled"));

        Assert.Equal(nextId, reconciled.CommandId);
        Assert.NotEqual(conflict.CommandId, reconciled.CommandId);
        Assert.Equal(conflict.CaseId, reconciled.CaseId);
        Assert.Equal(conflict.WorkspaceId, reconciled.WorkspaceId);
        Assert.Equal(conflict.LatestAggregateVersion, reconciled.BaseAggregateVersion);
        Assert.Null(reconciled.DependsOnCommandId);
        Assert.Equal(Payload("human-reconciled"), reconciled.ProposalPayload);
    }

    private static LocalCommandProposal Proposal(
        Guid caseId,
        long baseVersion = 1,
        Guid? dependencyId = null) => new(
        Guid.NewGuid(), caseId, "workspace-a", "RecordObservation", baseVersion,
        dependencyId, Payload("proposal"));

    private static byte[] Payload(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class OutboxFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"swiftaid-outbox-{Guid.NewGuid():N}");

        public OutboxFixture()
        {
            Directory.CreateDirectory(_directory);
            Clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-27T10:00:00Z"));
            Access = new MutableAccessContextProvider(new LocalDraftAccessContext(
                "actor-a", true, true, 1, Clock.GetUtcNow().AddHours(1), ["workspace-a"]));
            Outbox = CreateOutbox();
        }

        public MutableTimeProvider Clock { get; }
        public MutableAccessContextProvider Access { get; }
        public TestKeyStore Keys { get; } = new();
        public CommandOutbox Outbox { get; }
        public string DatabasePath => Path.Combine(_directory, "outbox.sqlite");
        public SyncAccessResult CurrentAuthority
        {
            get
            {
                var context = Access.Context;
                var ready = context is { IsProvisioned: true, IsActive: true } &&
                    context.ExpiresAtUtc > Clock.GetUtcNow();
                return new SyncAccessResult(
                    ready,
                    context?.ActorId,
                    context?.AccessEpoch ?? -1,
                    context?.PermittedWorkspaceIds.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal));
            }
        }

        public CommandOutbox CreateOutbox() => new(
            new CommandOutboxDbContextFactory(DatabasePath), Access, Keys, Clock, () => 0.5d);

        public CommandOutboxDbContext CreateDbContext() =>
            new CommandOutboxDbContextFactory(DatabasePath).CreateDbContext();

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _now = initialTime;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class MutableAccessContextProvider(LocalDraftAccessContext? context) : ILocalDraftAccessContextProvider
    {
        public LocalDraftAccessContext? Context { get; set; } = context;

        public ValueTask<LocalDraftAccessContext?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Context);
        }
    }

    private sealed class TestKeyStore : ILocalDraftKeyStore
    {
        public ValueTask<byte[]> GetOrCreateKeyAsync(string actorId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        }
    }

    private sealed class ReadyAccessRefresher : ISyncAccessRefresher
    {
        public Task<SyncAccessResult> RefreshAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SyncAccessResult(true, "actor-a", 1, new HashSet<string>(["workspace-a"], StringComparer.Ordinal)));
    }

    private sealed class FixedAccessRefresher(SyncAccessResult result) : ISyncAccessRefresher
    {
        public Task<SyncAccessResult> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class PayloadBinder : ICaseCommandPayloadBinder
    {
        public Task<byte[]> BindAsync(
            LocalCommandProposal proposal,
            long expectedAggregateVersion,
            CancellationToken cancellationToken)
        {
            var bound = Encoding.UTF8.GetBytes($"{proposal.CommandId:D}|{expectedAggregateVersion}|{Encoding.UTF8.GetString(proposal.ProposalPayload)}");
            return Task.FromResult(bound);
        }
    }

    private sealed class RecordingTransport : ICaseCommandTransport
    {
        public List<OutboundCaseCommand> Sent { get; } = [];
        public CaseCommandDeliveryResult Result { get; init; } = new(CaseCommandDeliveryStatus.TemporarilyUnavailable);

        public Task<CaseCommandDeliveryResult> SendAsync(OutboundCaseCommand command, CancellationToken cancellationToken)
        {
            Sent.Add(command with { Payload = command.Payload.ToArray(), PayloadHash = command.PayloadHash.ToArray() });
            return Task.FromResult(Result);
        }
    }

    private sealed class CommitThenTimeoutTransport : ICaseCommandTransport
    {
        private readonly HashSet<Guid> _committedCommands = [];

        public int BusinessAcceptances { get; private set; }
        public List<OutboundCaseCommand> Sent { get; } = [];

        public Task<CaseCommandDeliveryResult> SendAsync(OutboundCaseCommand command, CancellationToken cancellationToken)
        {
            Sent.Add(command with { Payload = command.Payload.ToArray(), PayloadHash = command.PayloadHash.ToArray() });
            if (_committedCommands.Add(command.CommandId))
            {
                BusinessAcceptances++;
                return Task.FromResult(new CaseCommandDeliveryResult(CaseCommandDeliveryStatus.TemporarilyUnavailable));
            }

            return Task.FromResult(new CaseCommandDeliveryResult(
                CaseCommandDeliveryStatus.Accepted, AcceptedAggregateVersion: 2));
        }
    }

    private sealed class StaticProjectionRefresher(ProjectionRefreshResult result) : IAuthorizedProjectionRefresher
    {
        public Task<ProjectionRefreshResult> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
