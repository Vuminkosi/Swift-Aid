using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiGatewayClientScopeTests
{
    [Fact]
    public async Task OverlappingHistoryReadsHaveIndependentDisposedStoreScopes()
    {
        var gate = new ReadGate();
        await using var services = new ServiceCollection().AddSingleton(gate)
            .AddScoped<IEllaiSessionStore, BlockingStore>().BuildServiceProvider();
        await using var circuit = services.CreateAsyncScope();
        var client = new EllaiReviewGatewayClient(new SignedIn(), circuit.ServiceProvider);
        var first = client.ListSessionHistoryAsync(0, CancellationToken.None);
        var second = client.ListSessionHistoryAsync(0, CancellationToken.None);
        try { await gate.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { gate.Release.TrySetResult(); }
        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.NotNull(result));
        Assert.Equal(2, gate.Instances.Count);
        Assert.Equal(2, gate.Disposed);
    }

    private sealed class ReadGate
    {
        public ConcurrentDictionary<Guid, byte> Instances { get; } = new();
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Started;
        public int Disposed;
    }

    private sealed class BlockingStore(ReadGate gate) : IEllaiSessionStore, IAsyncDisposable
    {
        private readonly Guid _id = Guid.NewGuid();
        public async Task<IReadOnlyList<EllaiSessionRecord>> ListOwnedAsync(string owner, int offset, int take, CancellationToken ct)
        {
            gate.Instances.TryAdd(_id, 0);
            if (Interlocked.Increment(ref gate.Started) == 2) gate.BothStarted.TrySetResult();
            await gate.Release.Task.WaitAsync(ct);
            return [];
        }
        public Task<EllaiSessionRecord?> GetAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<EllaiSessionRecord> CreateAsync(EllaiSessionRecord record, CancellationToken ct) => throw new NotSupportedException();
        public Task<EllaiSessionRecord?> TryUpdateAsync(EllaiSessionRecord record, long version, CancellationToken ct) => throw new NotSupportedException();
        public Task<EllaiUtteranceStoreResult?> TryAcceptUtteranceAsync(EllaiSessionRecord record, long version, string id,
            CancellationToken ct, string? outboxPayloadJson = null) => throw new NotSupportedException();
        public Task SaveCheckpointAsync(EllaiCheckpoint checkpoint, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Interlocked.Increment(ref gate.Disposed); return ValueTask.CompletedTask; }
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "ellai-scope-test"),
                new Claim(ActorContext.AccessEpochClaimType, "1")], "test"))));
    }
}
