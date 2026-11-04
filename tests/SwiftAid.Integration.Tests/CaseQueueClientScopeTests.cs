using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Contracts.Cases;
using SwiftAid.Gateway.Cases;

namespace SwiftAid.Integration.Tests;

public sealed class CaseQueueClientScopeTests
{
    [Fact]
    public async Task OverlappingReadsOwnSeparateQueryScopesAndDisposeThem()
    {
        var gate = new ReadGate();
        await using var services = new ServiceCollection()
            .AddSingleton(gate)
            .AddScoped<ICaseQueueQuery, BlockingQuery>()
            .BuildServiceProvider();
        await using var circuit = services.CreateAsyncScope();
        var client = new CaseQueueGatewayClient(new SignedIn(), circuit.ServiceProvider);
        var first = client.GetMineAsync(10);
        var second = client.GetMineAsync(10);
        try
        {
            await gate.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Equal(CaseQueueAccess.Allowed, result.Access));
        Assert.Equal(2, gate.Instances.Count);
        Assert.Equal(2, gate.Disposed);
    }

    [Fact]
    public async Task BlazorQueueClientForwardsCursorToTheFreshScopedQuery()
    {
        var query = new RecordingQuery();
        await using var services = new ServiceCollection()
            .AddSingleton<ICaseQueueQuery>(query)
            .BuildServiceProvider();
        await using var circuit = services.CreateAsyncScope();
        var client = new CaseQueueGatewayClient(new SignedIn(), circuit.ServiceProvider);

        var result = await client.GetMineAsync(50, "cursor-token");

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal("cursor-token", query.LastCursor);
        Assert.Equal(50, query.LastLimit);
        Assert.NotNull(query.LastFilter);
        Assert.Equal(TimeSpan.FromDays(30), query.LastFilter.ReportedBeforeUtc - query.LastFilter.ReportedFromUtc);
    }

    private sealed class ReadGate
    {
        public ConcurrentDictionary<Guid, byte> Instances { get; } = new();
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Started;
        public int Disposed;
    }

    private sealed class BlockingQuery(ReadGate gate) : ICaseQueueQuery, IAsyncDisposable
    {
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, string? cursor, CancellationToken ct) =>
            GetMineAsync(actor, limit, ct);
        private readonly Guid _id = Guid.NewGuid();
        public async Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct)
        {
            gate.Instances.TryAdd(_id, 0);
            if (Interlocked.Increment(ref gate.Started) == 2) gate.BothStarted.TrySetResult();
            await gate.Release.Task.WaitAsync(ct);
            return new(CaseQueueAccess.Allowed, []);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref gate.Disposed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingQuery : ICaseQueueQuery
    {
        public CaseQueueFilter? LastFilter { get; private set; }
        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CaseQueueFilter filter, string? cursor, CancellationToken ct)
        {
            LastFilter = filter;
            return GetMineAsync(actor, limit, cursor, ct);
        }
        public string? LastCursor { get; private set; }
        public int? LastLimit { get; private set; }

        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, CancellationToken ct)
        {
            LastLimit = limit;
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, []));
        }

        public Task<CaseQueueResult> GetMineAsync(ActorContext actor, int? limit, string? cursor, CancellationToken ct)
        {
            LastCursor = cursor;
            LastLimit = limit;
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, []));
        }
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "scope-test-actor"),
                new Claim(ActorContext.AccessEpochClaimType, "1")], "test"))));
    }
}
