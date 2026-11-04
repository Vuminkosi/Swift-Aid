using SwiftAid.Application.Authorization;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T06")]
public sealed class EllaiUtteranceQueueTests
{
    private static EllaiUtteranceWork Work(string session = "session-1", string utterance = "Hello.") => new(
        session, "epoch-1", utterance, "cases", "case-1", EllaiMode.Ask, 1, "cycle-1",
        TestActor(), true, "consent/1");

    private static ActorContext TestActor()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[]
                {
                    new System.Security.Claims.Claim(
                        System.Security.Claims.ClaimTypes.NameIdentifier, "officer-1"),
                    new System.Security.Claims.Claim(
                        ActorContext.AccessEpochClaimType, "7"),
                },
                "test"));
        return ActorContext.FromValidatedPrincipal(principal);
    }

    [Fact]
    public async Task EnqueueDequeueCompleteRoundTrips()
    {
        var queue = new EllaiUtteranceQueue();

        Assert.True(queue.TryEnqueue(Work()));
        var item = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("Hello.", item.Utterance);
        queue.Complete(item.SessionId, item);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task NewerUtteranceReplacesPendingAndRunsNext()
    {
        var queue = new EllaiUtteranceQueue();

        Assert.True(queue.TryEnqueue(Work(utterance: "First.")));
        Assert.True(queue.TryEnqueue(Work(utterance: "Second.")));

        var item = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("Second.", item.Utterance);
        queue.Complete(item.SessionId, item);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task MidRunUtteranceRequeuesForItsOwnRun()
    {
        var queue = new EllaiUtteranceQueue();
        Assert.True(queue.TryEnqueue(Work(utterance: "First.")));

        var first = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(Work(utterance: "Second.")));
        queue.Complete(first.SessionId, first);

        var second = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("Second.", second.Utterance);
        queue.Complete(second.SessionId, second);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task SessionsAreIndependent()
    {
        var queue = new EllaiUtteranceQueue();
        Assert.True(queue.TryEnqueue(Work("session-1")));
        Assert.True(queue.TryEnqueue(Work("session-2")));

        var first = await queue.DequeueAsync(CancellationToken.None);
        var second = await queue.DequeueAsync(CancellationToken.None);
        Assert.NotEqual(first.SessionId, second.SessionId);
        queue.Complete(first.SessionId, first);
        queue.Complete(second.SessionId, second);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void FullQueueRefusesNewSessions()
    {
        var queue = new EllaiUtteranceQueue(capacity: 1);

        Assert.True(queue.TryEnqueue(Work("session-1")));
        Assert.False(queue.TryEnqueue(Work("session-2")));
        Assert.True(queue.TryEnqueue(Work("session-1", "Updated.")));
    }
}
