using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T04")]
public sealed class EllaiUtteranceOutboxTests
{
    [Fact]
    public async Task AcceptedPayloadIsClaimedOnceAndRecoverableAfterLeaseExpiry()
    {
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        await AcceptAsync(store, "utterance-1", 1, "safe prompt");

        var first = await store.ClaimNextAsync(now, now.AddSeconds(30), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("safe prompt", first!.PayloadJson);
        Assert.Equal(1, first.AttemptCount);
        Assert.Null(await store.ClaimNextAsync(now.AddSeconds(10), now.AddSeconds(40), CancellationToken.None));

        var recovered = await store.ClaimNextAsync(now.AddSeconds(31), now.AddSeconds(61), CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal(first.ClientUtteranceId, recovered!.ClientUtteranceId);
        Assert.Equal(first.PayloadJson, recovered.PayloadJson);
        Assert.Equal(2, recovered.AttemptCount);

        await store.CompleteAsync(recovered.SessionId, recovered.ClientUtteranceId, recovered.AttemptCount,
            now.AddSeconds(32), CancellationToken.None);
        Assert.Null(await store.ClaimNextAsync(now.AddMinutes(1), now.AddMinutes(2), CancellationToken.None));
    }

    [Fact]
    public async Task IdempotentReplayCannotReplaceTheAcceptedOutboxPayload()
    {
        var store = await CreateStoreAsync();
        await AcceptAsync(store, "utterance-replay", 1, "approved safe prompt");

        var replay = await store.TryAcceptUtteranceAsync(
            Session(version: 2, cycles: 1), 1, "utterance-replay", CancellationToken.None,
            "replacement prompt");

        Assert.NotNull(replay);
        Assert.True(replay!.Replay);
        var queued = await store.ClaimNextAsync(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.NotNull(queued);
        Assert.Equal("approved safe prompt", queued!.PayloadJson);
    }

    [Fact]
    public async Task WorkForOneSessionIsClaimedInOrderWithoutConcurrentClaims()
    {
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        await AcceptAsync(store, "utterance-first", 1, "first safe prompt");
        await AcceptAsync(store, "utterance-second", 2, "second safe prompt");

        var first = await store.ClaimNextAsync(now, now.AddMinutes(1), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("utterance-first", first!.ClientUtteranceId);
        Assert.Null(await store.ClaimNextAsync(now, now.AddMinutes(1), CancellationToken.None));

        await store.CompleteAsync(first.SessionId, first.ClientUtteranceId, first.AttemptCount,
            now.AddSeconds(1), CancellationToken.None);
        var second = await store.ClaimNextAsync(now.AddSeconds(2), now.AddMinutes(1), CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal("utterance-second", second!.ClientUtteranceId);
    }

    [Fact]
    public async Task FailedOutboxWorkIsBackedOffAndTerminalFailureClearsPayload()
    {
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        await AcceptAsync(store, "utterance-retry", 1, "safe prompt");
        var claimed = await store.ClaimNextAsync(now, now.AddSeconds(20), CancellationToken.None);
        Assert.NotNull(claimed);

        var retryAt = now.AddSeconds(30);
        await store.FailAsync(claimed!.SessionId, claimed.ClientUtteranceId, claimed.AttemptCount,
            retryAt, terminal: false, CancellationToken.None);
        Assert.Null(await store.ClaimNextAsync(now.AddSeconds(25), now.AddSeconds(45), CancellationToken.None));

        var retry = await store.ClaimNextAsync(retryAt, retryAt.AddSeconds(20), CancellationToken.None);
        Assert.NotNull(retry);
        Assert.Equal(2, retry!.AttemptCount);
        await store.FailAsync(retry.SessionId, retry.ClientUtteranceId, retry.AttemptCount,
            retryAt.AddSeconds(1), terminal: true, CancellationToken.None);

        Assert.Null(await store.ClaimNextAsync(retryAt.AddMinutes(1), retryAt.AddMinutes(2), CancellationToken.None));
    }

    private static async Task<InMemoryEllaiSessionStore> CreateStoreAsync()
    {
        var store = new InMemoryEllaiSessionStore();
        await store.CreateAsync(Session(version: 1, cycles: 0), CancellationToken.None);
        return store;
    }

    private static async Task AcceptAsync(
        InMemoryEllaiSessionStore store, string utteranceId, long expectedVersion, string payload)
    {
        var accepted = await store.TryAcceptUtteranceAsync(
            Session(version: expectedVersion + 1, cycles: (int)expectedVersion),
            expectedVersion, utteranceId, CancellationToken.None, payload);
        Assert.NotNull(accepted);
        Assert.False(accepted!.Replay);
    }

    private static EllaiSessionRecord Session(long version, int cycles) => new(
        "ellai-outbox-test", EllaiMode.Ask, "Active", version, 1, version, 1,
        cycles, 12, "actor-one", "scope-1", null, null, DateTimeOffset.UtcNow);
}
