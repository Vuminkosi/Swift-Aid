using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
public sealed class EllaiSessionResponseStoreTests
{
    [Fact]
    public void ProvisionalDeliveryRequiresCurrentAttemptLeaseAndEpoch()
    {
        var store = new EllaiSessionResponseStore();
        var generation = store.CaptureGeneration("session");
        Assert.True(store.PublishProvisional("session", generation, "turn", 1, "scope", 7, 1, "Segment."));
        var response = Assert.Single(store.ReadSince("session", 0).Responses);
        var now = DateTimeOffset.UtcNow;
        var current = new EllaiConversationTurn("session", "turn", 1, "question", null, "Processing", 1, null)
            { LeaseExpiresAtUtc = now.AddMinutes(1) };
        Assert.True(store.CanDeliver("session", response, "scope", 7, [current], now));
        Assert.False(store.CanDeliver("session", response, "other", 7, [current], now));
        Assert.False(store.CanDeliver("session", response, "scope", 8, [current], now));
        Assert.False(store.CanDeliver("session", response, "scope", 7, [current with { AttemptCount = 2 }], now));
        Assert.False(store.CanDeliver("session", response, "scope", 7, [current with { LeaseExpiresAtUtc = now.AddSeconds(-1) }], now));
        Assert.False(store.CanDeliver("session", response, "scope", 7, [current with { Outcome = "TerminalFailure" }], now));
        Assert.True(store.CanDeliver("session", response, "scope", 7, [current with { Outcome = "Completed", CompletedAtUtc = now, LeaseExpiresAtUtc = null }], now));
    }

    [Fact]
    public void ResponsesAreSessionScopedBoundedAndCleared()
    {
        var store = new EllaiSessionResponseStore();
        var first = Assert.IsType<SwiftAid.Contracts.Ellai.EllaiSessionResponseDto>(
            store.Publish("session-1", "utterance-1", "A model-generated answer."));
        store.Publish("session-2", "utterance-2", "Different session.");

        var firstPage = store.ReadSince("session-1", 0);
        Assert.Equal(first.Sequence, Assert.Single(firstPage.Responses).Sequence);
        Assert.Empty(store.ReadSince("session-2", first.Sequence).Responses);

        store.Clear("session-1");
        Assert.Empty(store.ReadSince("session-1", 0).Responses);
    }

    [Fact]
    public void ReplayingCompletedUtteranceDoesNotPublishDuplicateResponse()
    {
        var store = new EllaiSessionResponseStore();

        var first = store.Publish("session-1", "utterance-1", "Answer.");
        var retry = store.Publish("session-1", "utterance-1", "Answer.");

        Assert.Equal(first, retry);
        Assert.Single(store.ReadSince("session-1", 0).Responses);
    }

    [Fact]
    public void RejectsBlankAndOversizedModelText()
    {
        var store = new EllaiSessionResponseStore();

        Assert.Null(store.Publish("session-1", "utterance-1", "  "));
        Assert.Null(store.Publish("session-1", "utterance-2", new string('x', EllaiSessionResponseStore.MaximumTextLength + 1)));
        Assert.Empty(store.ReadSince("session-1", 0).Responses);
    }

    [Fact]
    public void PublishesOrderedResponseChunksAndAnUtteranceCompletionEvent()
    {
        var store = new EllaiSessionResponseStore();

        var first = store.PublishChunk("session-1", "utterance-1", 1, "First sentence.");
        var second = store.PublishChunk("session-1", "utterance-1", 2, "Second sentence.");
        var completed = store.CompleteUtterance("session-1", "utterance-1");
        var completionRetry = store.CompleteUtterance("session-1", "utterance-1");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(completed);
        Assert.True(first.Sequence < second.Sequence);
        Assert.True(second.Sequence < completed.Sequence);
        Assert.Equal("utterance-1", second.ClientUtteranceId);
        Assert.False(second.IsFinal);
        Assert.True(completed.IsFinal);
        Assert.Equal(completed, completionRetry);
        Assert.Empty(completed.Text);
        Assert.Equal(3, store.ReadSince("session-1", 0).Responses.Count);
    }

    [Fact]
    public void ReplayingAnEarlierChunkDoesNotPublishItAgain()
    {
        var store = new EllaiSessionResponseStore();
        var first = store.PublishChunk("session-1", "utterance-1", 1, "First sentence.");
        store.PublishChunk("session-1", "utterance-1", 2, "Second sentence.");
        var replay = store.PublishChunk("session-1", "utterance-1", 1, "First sentence.");

        Assert.Equal(first, replay);
        Assert.Equal(2, store.ReadSince("session-1", 0).Responses.Count);
        Assert.NotEqual(first, store.PublishChunk("session-2", "utterance-1", 1, "First sentence."));
    }

    [Fact]
    public void EvictedChunkCanBePublishedAgain()
    {
        var store = new EllaiSessionResponseStore();
        var first = store.PublishChunk("session-1", "utterance-1", 1, "First.");
        for (var index = 2; index <= EllaiSessionResponseStore.MaximumResponsesPerSession + 1; index++)
            store.PublishChunk("session-1", "utterance-1", index, "Later.");

        var replay = store.PublishChunk("session-1", "utterance-1", 1, "First.");
        Assert.NotNull(first);
        Assert.NotNull(replay);
        Assert.True(replay.Sequence > first.Sequence);
        Assert.Equal(EllaiSessionResponseStore.MaximumResponsesPerSession,
            store.ReadSince("session-1", 0, EllaiSessionResponseStore.MaximumResponsesPerSession).Responses.Count);
    }

    [Fact]
    public void ClearedSessionRejectsPublicationFromOldGeneration()
    {
        var store = new EllaiSessionResponseStore();
        var oldGeneration = store.CaptureGeneration("session-1");
        store.Clear("session-1");
        var currentGeneration = store.CaptureGeneration("session-1");
        var completed = EllaiUtteranceResolution.Completed("Synthetic answer.", 1);

        Assert.False(store.PublishCommitted("session-1", oldGeneration, "old-turn", completed, ["Synthetic answer."]));
        Assert.Empty(store.ReadSince("session-1", 0).Responses);
        Assert.True(store.PublishCommitted("session-1", currentGeneration, "new-turn", completed, ["Synthetic answer."]));
        Assert.Equal(2, store.ReadSince("session-1", 0).Responses.Count);
    }

    [Fact]
    public void CommittedPublicationRejectsMismatchedChunksBeforePublishing()
    {
        var store = new EllaiSessionResponseStore();
        var generation = store.CaptureGeneration("session-1");

        Assert.Throws<ArgumentException>(() => store.PublishCommitted("session-1", generation, "turn",
            EllaiUtteranceResolution.Completed("Committed answer.", 1), ["Different answer."]));
        Assert.Empty(store.ReadSince("session-1", 0).Responses);
    }
}
