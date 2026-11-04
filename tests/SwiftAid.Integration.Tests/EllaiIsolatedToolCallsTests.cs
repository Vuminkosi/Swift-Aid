using SwiftAid.Gateway.Ellai;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using System.Security.Claims;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Application.Cases.Commands;
using System.Collections.Immutable;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiIsolatedToolCallsTests
{
    [FrameworkPostgresFact]
    public async Task CancelledIsolatedReadCannotRetainPostgresAuthorityLockPastDeadline()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var calls = new EllaiIsolatedToolCalls(1, TimeSpan.FromSeconds(2));
        var run = calls.RunAsync(async token =>
        {
            try
            {
                await using var db = fixture.CreateDbContext();
                var unit = new PostgresCaseCommandUnitOfWork(db);
                var lease = await unit.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId,
                    ImmutableArray<string>.Empty, CaseLeaseMode.Read, token);
                try { started.TrySetResult(); await release.Task; }
                finally
                {
                    try { await lease.DisposeAsync(); }
                    catch (Exception error) when (error is Npgsql.NpgsqlException or InvalidOperationException or ObjectDisposedException) { }
                }
                return true;
            }
            finally { finished.TrySetResult(); }
        }, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            await using var db = fixture.CreateDbContext();
            var writer = new PostgresCaseCommandUnitOfWork(db);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var write = writer.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId,
                ImmutableArray<string>.Empty, CaseLeaseMode.Write, timeout.Token).AsTask();
            await Task.Delay(200, timeout.Token);
            Assert.False(write.IsCompleted);
            await using var acquired = await write.WaitAsync(timeout.Token);
            Assert.True(acquired.IsActive);
            Assert.False(finished.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [FrameworkPostgresFact]
    public async Task IsolatedQueueRechecksCurrentPostgresCaseAuthority()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        var registrations = new ServiceCollection().AddScoped(_ => fixture.CreateDbContext());
        registrations.AddScoped<ICaseQueueQuery>(sp =>
        {
            var db = sp.GetRequiredService<SwiftAidDbContext>();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            return new CaseQueueQuery(new PostgresCaseQueueStore(db),
                new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)), unit);
        });
        await using var provider = registrations.BuildServiceProvider();
        var isolated = new EllaiIsolatedCaseQueueQuery(provider, new EllaiIsolatedToolCalls());
        var actor = fixture.CreateActor();
        var first = await isolated.GetMineAsync(actor, 20, new CaseQueueFilter(CountryCode: "SZ"), default);
        Assert.Equal(CaseQueueAccess.Allowed, first.Access);
        Assert.Contains(first.Items, item => item.CaseId == fixture.CaseId);
        await fixture.SetGrantExpiryAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        var afterExpiry = await isolated.GetMineAsync(actor, 20, new CaseQueueFilter(CountryCode: "SZ"), default);
        Assert.Equal(CaseQueueAccess.Allowed, afterExpiry.Access);
        Assert.DoesNotContain(afterExpiry.Items, item => item.CaseId == fixture.CaseId);
    }

    [Fact]
    public async Task IsolatedQueuePreservesActorLimitAndFilter()
    {
        var queue = new CapturingQueue();
        await using var provider = new ServiceCollection().AddSingleton<ICaseQueueQuery>(queue).BuildServiceProvider();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "queue-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var filter = new CaseQueueFilter("SZ", "Reported", true);
        var isolated = new EllaiIsolatedCaseQueueQuery(provider, new EllaiIsolatedToolCalls());

        var result = await isolated.GetMineAsync(actor, 17, filter, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Same(actor, queue.Actor);
        Assert.Equal(17, queue.Limit);
        Assert.Equal(filter, queue.Filter);
    }

    [Fact]
    public async Task IsolatedSearchForwardsActorFiltersAndBothCursors()
    {
        var queue = new CapturingQueue();
        await using var provider = new ServiceCollection().AddSingleton<ICaseQueueQuery>(queue).BuildServiceProvider();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "searcher"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var filter = new CaseQueueFilter("SZ", NewestReportedFirst: true);
        var result = await new EllaiIsolatedCaseQueueQuery(provider, new EllaiIsolatedToolCalls())
            .GetSearchableAsync(actor, 50, filter, "previous-page", default);
        Assert.True(queue.Searched);
        Assert.Same(actor, queue.Actor);
        Assert.Equal(50, queue.Limit);
        Assert.Equal(filter, queue.Filter);
        Assert.Equal("previous-page", queue.Cursor);
        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal("following-page", result.NextCursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedQueueForwardsContinuation(bool filtered)
    {
        var queue = new CapturingQueue();
        await using var provider = new ServiceCollection().AddSingleton<ICaseQueueQuery>(queue).BuildServiceProvider();
        var actor = ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "queue-owner"), new Claim(ActorContext.AccessEpochClaimType, "1")], "test")));
        var filter = new CaseQueueFilter("SZ", NewestReportedFirst: true);
        var isolated = new EllaiIsolatedCaseQueueQuery(provider, new EllaiIsolatedToolCalls());
        var result = filtered
            ? await isolated.GetMineAsync(actor, 50, filter, "next-page", default)
            : await isolated.GetMineAsync(actor, 50, "next-page", default);
        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal("next-page", queue.Cursor);
        Assert.Equal(filtered ? filter : null, queue.Filter);
        Assert.Same(actor, queue.Actor);
        Assert.Equal(50, queue.Limit);
    }

    private sealed class CapturingQueue : ICaseQueueQuery
    {
        public bool Searched { get; private set; }
        public Task<CaseQueueResult> GetSearchableAsync(ActorContext actor, int? limit, CaseQueueFilter? filter, string? cursor, CancellationToken ct)
        {
            Searched = true;
            Actor = actor; Limit = limit; Filter = filter; Cursor = cursor;
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, [], "following-page"));
        }
        public ActorContext? Actor { get; private set; }
        public int? Limit { get; private set; }
        public CaseQueueFilter? Filter { get; private set; }
        public string? Cursor { get; private set; }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct) =>
            GetMineAsync(actor, limit, new CaseQueueFilter(), ct);
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, CancellationToken ct)
        {
            Actor = actor;
            Limit = limit;
            Filter = filter;
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, []));
        }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, string? cursor, CancellationToken ct)
        {
            Actor = actor; Limit = limit; Filter = null; Cursor = cursor;
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, []));
        }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, string? cursor, CancellationToken ct)
        {
            Actor = actor; Limit = limit; Filter = filter; Cursor = cursor;
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, []));
        }
    }

    [Theory]
    [InlineData(EllaiToolNames.State, false)]
    [InlineData(EllaiToolNames.Topic, true)]
    [InlineData(EllaiToolNames.Evidence, true)]
    [InlineData("unregistered-future-tool", false)]
    [InlineData(EllaiToolNames.OwnedCaseCount, true)]
    [InlineData(EllaiToolNames.ClassificationEvidence, true)]
    [InlineData(EllaiToolNames.Receipts, true)]
    [InlineData(EllaiToolNames.ImportDestinations, true)]
    [InlineData(EllaiToolNames.Imports, true)]
    [InlineData(EllaiToolNames.ImportStatus, true)]
    [InlineData(EllaiToolNames.UiCatalogue, true)]
    [InlineData(EllaiToolNames.UiObserve, true)]
    [InlineData(EllaiToolNames.Locations, false)]
    public void OnlyExplicitReadOnlyToolsCanOutliveTurn(string name, bool expected) =>
        Assert.Equal(expected, EllaiUtteranceWorker.CanDetachReadOnlyTool(
            new EllaiToolRequest(name, name == EllaiToolNames.State
                ? new Dictionary<string, string> { ["projection"] = "queue" }
                : new Dictionary<string, string>())));

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("feature", false, true)]
    [InlineData("FEATURE", false, true)]
    [InlineData("feature", true, false)]
    [InlineData(null, true, false)]
    [InlineData("queue", false, false)]
    [InlineData("unknown", false, false)]
    public async Task StateIsolationDetachesOnlyStaticFeatureReads(string? projection, bool extraArgument, bool readOnly)
    {
        var arguments = new Dictionary<string, string>();
        if (projection is not null) arguments["projection"] = projection;
        if (extraArgument) arguments["other"] = "value";
        var request = new EllaiToolRequest(EllaiToolNames.State, arguments);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var run = EllaiUtteranceWorker.InvokeToolWithIsolation(request, async _ =>
        {
            started.TrySetResult();
            await release.Task;
            finished.TrySetResult();
            return 42;
        }, new EllaiIsolatedToolCalls(1), cancellation.Token);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            if (readOnly)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            else
                Assert.False(run.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        if (!readOnly) Assert.Equal(42, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("queue", false, true)]
    [InlineData("QUEUE", false, true)]
    [InlineData("queue", true, false)]
    [InlineData("feature", false, false)]
    [InlineData(null, false, false)]
    public void FencedQueueSelectionRejectsUnrecognizedShapes(string? projection, bool extraArgument, bool expected)
    {
        var arguments = new Dictionary<string, string>();
        if (projection is not null) arguments["projection"] = projection;
        if (extraArgument) arguments["unexpected"] = "value";
        Assert.Equal(expected, EllaiUtteranceWorker.IsFencedQueueRequest(new(EllaiToolNames.State, arguments)));
    }

    [Fact]
    public async Task CancelledCallerDoesNotDisposeRunningOperationsScope()
    {
        var calls = new EllaiIsolatedToolCalls(maximumConcurrent: 1);
        var created = new TaskCompletionSource<ScopedProbe>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection().AddScoped<ScopedProbe>().BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var run = calls.RunAsync(async _ =>
        {
            await using var scope = provider.CreateAsyncScope();
            var resource = scope.ServiceProvider.GetRequiredService<ScopedProbe>();
            created.TrySetResult(resource);
            await release.Task;
            Assert.False(resource.Disposed.Task.IsCompleted);
            return 1;
        }, cancellation.Token);
        var probe = await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(probe.Disposed.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ScopedProbe : IAsyncDisposable
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask DisposeAsync() { Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWinsWhenOperationReturnsOrFaults(bool fault)
    {
        var calls = new EllaiIsolatedToolCalls(maximumConcurrent: 1);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calls.RunAsync<int>(_ =>
        {
            cancellation.Cancel();
            return fault ? Task.FromException<int>(new InvalidOperationException("fixture")) : Task.FromResult(1);
        }, cancellation.Token));
        Assert.Equal(2, await calls.RunAsync(_ => Task.FromResult(2), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationReturnsWhileOperationRetainsItsSlotAndResources()
    {
        var calls = new EllaiIsolatedToolCalls(maximumConcurrent: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var run = calls.RunAsync(async _ =>
        {
            try
            {
                started.TrySetResult();
                await release.Task;
                return "late result";
            }
            finally { finished.TrySetResult(); }
        }, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(finished.Task.IsCompleted);

            using var waitingCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var entered = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calls.RunAsync(_ =>
            {
                entered = true;
                return Task.FromResult("unexpected");
            }, waitingCancellation.Token));
            Assert.False(entered);
        }
        finally { release.TrySetResult(); }

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("next result", await calls.RunAsync(_ => Task.FromResult("next result"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FaultedOperationReleasesItsSlot()
    {
        var calls = new EllaiIsolatedToolCalls(maximumConcurrent: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => calls.RunAsync<int>(
            _ => Task.FromException<int>(new InvalidOperationException()), CancellationToken.None));
        Assert.Equal(1, await calls.RunAsync(_ => Task.FromResult(1), CancellationToken.None));
    }
}
