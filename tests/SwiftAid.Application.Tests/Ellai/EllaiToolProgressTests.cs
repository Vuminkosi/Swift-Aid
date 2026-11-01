using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiToolProgressTests
{
    private static EllaiActivityEvent Event(long sequence, EllaiActivityKind kind, string tool = EllaiToolNames.State) =>
        new("s", 2, sequence, kind, "cycle", "read", DateTimeOffset.UtcNow, tool);

    [Fact]
    public void SmallTalkHasNoNarrationAndProgressIsNotAnAnswer()
    {
        var store = new EllaiSessionResponseStore();
        var publisher = new EllaiToolProgressPublisher(store, "s", "u", 2, 1);
        publisher.Finish("Completed");
        Assert.Empty(store.ReadSince("s", 0).Responses);
        publisher = new(store, "s", "u", 2, 1);
        publisher.Observe(Event(1, EllaiActivityKind.ToolStarted));
        var ack = Assert.Single(store.ReadSince("s", 0).Responses);
        Assert.Equal("Acknowledgement", ack.Progress?.State);
        Assert.Equal("Let me check the records.", ack.Text);
        Assert.False(ack.IsFinal);
        Assert.False(store.ContainsUtterance("s", "u"));
        Assert.True(store.CanSpeakProgress(ack));
    }

    [Fact]
    public async Task ConcurrentAndChainedCallsAcknowledgeOnceAndDuplicateCallbacksAreIgnored()
    {
        var store = new EllaiSessionResponseStore();
        var publisher = new EllaiToolProgressPublisher(store, "s", "u", 2, 1);
        await Task.WhenAll(Enumerable.Range(1, 24).Select(index => Task.Run(() =>
            publisher.Observe(Event(index, EllaiActivityKind.ToolStarted)))));
        publisher.Observe(Event(24, EllaiActivityKind.ToolStarted));
        var rows = store.ReadSince("s", 0, 64).Responses;
        Assert.Single(rows, row => row.Progress?.State == "Acknowledgement");
        Assert.Single(rows, row => row.Progress?.Speak == true);
        Assert.Equal(rows.Count, rows.Select(row => row.Progress!.EventId).Distinct().Count());
    }

    [Fact]
    public void RetryAfterRestartDoesNotRepeatTheInitialAcknowledgement()
    {
        var store = new EllaiSessionResponseStore();
        new EllaiToolProgressPublisher(store, "s", "u", 2, 2).Observe(Event(1, EllaiActivityKind.ToolStarted));
        Assert.False(Assert.Single(store.ReadSince("s", 0).Responses).Progress!.Speak);
    }

    [Theory]
    [InlineData("Completed", "Completed")]
    [InlineData("TerminalFailure", "Failed")]
    [InlineData("Cancelled", "Cancelled")]
    [InlineData("Interrupted", "Interrupted")]
    [InlineData("RetryableFailure", "Retrying")]
    public void TerminalOutcomeFencesSpeechAndLateProgress(string outcome, string state)
    {
        var store = new EllaiSessionResponseStore();
        var publisher = new EllaiToolProgressPublisher(store, "s", "u", 2, 1);
        publisher.Observe(Event(1, EllaiActivityKind.ToolStarted));
        var ack = Assert.Single(store.ReadSince("s", 0).Responses);
        publisher.Finish(outcome);
        publisher.Observe(Event(2, EllaiActivityKind.ToolStarted));
        var rows = store.ReadSince("s", 0).Responses;
        Assert.Equal(2, rows.Count);
        Assert.Equal(state, rows[^1].Progress?.State);
        Assert.False(store.CanSpeakProgress(ack));
    }

    [Fact]
    public void SlowProgressIsBoundedVariedAndExpires()
    {
        var clock = new Clock();
        var store = new EllaiSessionResponseStore(clock);
        var publisher = new EllaiToolProgressPublisher(store, "s", "u", 2, 1, clock);
        publisher.Observe(Event(1, EllaiActivityKind.ToolStarted));
        Assert.Single(store.ReadSince("s", 0).Responses);
        for (var sequence = 3; sequence < 8; sequence++)
        {
            clock.Now = clock.Now.AddSeconds(20);
            publisher.Observe(Event(sequence, EllaiActivityKind.ToolPending));
        }
        var spoken = store.ReadSince("s", 0).Responses.Where(row => row.Progress!.Speak).ToArray();
        Assert.Equal(3, spoken.Length);
        Assert.Equal(3, spoken.Select(row => row.Text).Distinct().Count());
        Assert.All(spoken, row => Assert.False(store.CanSpeakProgress(row)));
    }

    [Theory]
    [InlineData("ss")]
    [InlineData("sn")]
    public void UnvalidatedLocalesUseApprovedEnglishFallback(string language)
    {
        Assert.Equal(EllaiToolProgressPublisher.Describe(EllaiToolNames.State, "u", "en"),
            EllaiToolProgressPublisher.Describe(EllaiToolNames.State, "u", language));
    }

    [Fact]
    public void ConfirmedInterruptionSilencesLaterProgressWithoutCancellingTheTool()
    {
        var clock = new Clock();
        var store = new EllaiSessionResponseStore(clock);
        var publisher = new EllaiToolProgressPublisher(store, "s", "u", 2, 1, clock);
        publisher.Observe(Event(1, EllaiActivityKind.ToolStarted));
        store.InterruptProgress("s", 1);
        store.InterruptProgress("s", 1);
        clock.Now = clock.Now.AddSeconds(9);
        publisher.Observe(Event(2, EllaiActivityKind.ToolPending));
        var rows = store.ReadSince("s", 0).Responses;
        Assert.Single(rows, row => row.Progress?.State == "Interrupted");
        Assert.False(store.CanSpeakProgress(rows[^1]));
        publisher.Finish("Completed");
        Assert.Equal("Completed", store.ReadSince("s", 0).Responses[^1].Progress?.State);
    }

    [Fact]
    public void PendingTimerBoundaryDoesNotLoseItsOnlySlowToolUpdate()
    {
        var clock = new Clock();
        var store = new EllaiSessionResponseStore(clock);
        var publisher = new EllaiToolProgressPublisher(store, "s", "u", 2, 1, clock);
        publisher.Observe(Event(1, EllaiActivityKind.ToolStarted));
        // Task.Delay and wall-clock sampling need not agree to a millisecond.
        clock.Now = clock.Now.AddMilliseconds(7_999);
        publisher.Observe(Event(2, EllaiActivityKind.ToolPending));
        Assert.Equal(2, store.ReadSince("s", 0).Responses.Count);
    }

    [Fact]
    public void LegacyFeedSkipsProgressBeforePaginationSoItsCursorCannotStall()
    {
        var store = new EllaiSessionResponseStore();
        var publisher = new EllaiToolProgressPublisher(store, "s", "u", 2, 1);
        for (var sequence = 1; sequence <= 24; sequence++) publisher.Observe(Event(sequence, EllaiActivityKind.ToolStarted));
        store.Reconcile("s", "u", EllaiUtteranceResolution.Completed("The answer.", 2));
        var legacy = store.ReadSince("s", 0, includeProgress: false);
        Assert.Equal("The answer.", Assert.Single(legacy.Responses).Text);
        Assert.Equal(25, legacy.Cursor);
    }

    [Fact]
    public void ApprovalRequiredOnlyPublishesAnAwaitingUserActionState()
    {
        var store = new EllaiSessionResponseStore();
        var publisher = new EllaiToolProgressPublisher(store, "s", "u", 2, 1);
        publisher.Observe(Event(1, EllaiActivityKind.ApprovalRequired));
        var row = Assert.Single(store.ReadSince("s", 0).Responses);
        Assert.Equal("ApprovalRequired", row.Progress?.State);
        Assert.False(row.Progress!.Speak);
        Assert.False(row.IsFinal);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
