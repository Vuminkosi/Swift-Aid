using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T11")]
public sealed class EllaiVoicePipelineTests
{
    [Fact]
    public void LateSpeechCompleteCannotOverwriteLaterTurn()
    {
        var normalizer = new TranscriptTurnNormalizer();

        var first = normalizer.Normalize(new[]
        {
            new TranscriptTransportEvent("pt-1", TranscriptEventKind.Partial, "hel", 1),
            new TranscriptTransportEvent("pt-1", TranscriptEventKind.SpeechEnd, "hello", 1),
            new TranscriptTransportEvent("pt-1", TranscriptEventKind.Completed, "hello", 1),
        }, 1);

        Assert.Single(first);

        var duplicate = normalizer.Normalize(new[]
        {
            new TranscriptTransportEvent("pt-1", TranscriptEventKind.Completed, "hello", 1),
        }, 1);

        Assert.Empty(duplicate);

        var second = normalizer.Normalize(new[]
        {
            new TranscriptTransportEvent("pt-2", TranscriptEventKind.Completed, "second question", 1),
        }, 1);

        Assert.Single(second);
        Assert.Equal("second question", second[0].Text);
    }

    [Fact]
    public void PartialAndSpeechEndNeverYieldUtterances()
    {
        var normalizer = new TranscriptTurnNormalizer();

        var result = normalizer.Normalize(new[]
        {
            new TranscriptTransportEvent("pt-9", TranscriptEventKind.Partial, "partial text", 1),
            new TranscriptTransportEvent("pt-9", TranscriptEventKind.SpeechEnd, "partial text", 1),
        }, 1);

        Assert.Empty(result);
    }

    [Fact]
    public void InterruptionDropsQueuedNarration()
    {
        var queue = new LocalNarrationQueue();
        var epoch = queue.SpeechEpoch;

        Assert.True(queue.TryEnqueue(new NarrationSentence("s1", "First.", "sess", "t1", epoch, "v1")));
        Assert.True(queue.TryEnqueue(new NarrationSentence("s2", "Second.", "sess", "t1", epoch, "v1")));
        Assert.False(queue.TryEnqueue(new NarrationSentence("s3", "Third.", "sess", "t1", epoch, "v1")));

        var newEpoch = queue.Interrupt();
        Assert.Equal(epoch + 1, newEpoch);

        Assert.Null(queue.Dequeue(epoch));
        Assert.Null(queue.Dequeue(newEpoch));
    }
}
