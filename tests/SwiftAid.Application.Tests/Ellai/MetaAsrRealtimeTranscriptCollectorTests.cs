using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
public sealed class MetaAsrRealtimeTranscriptCollectorTests
{
    [Fact]
    public void FirstZeroBasedProviderTurnIsDeliveredOnceWithoutClosingTheStream()
    {
        var collector = new MetaAsrRealtimeTranscriptCollector();

        Assert.True(collector.TryReadPartial(
            """{"type":"transcript","turnId":0,"final":false,"transcript":"Hello"}""",
            out var partial));
        Assert.Equal(0, partial?.TurnId);
        Assert.True(collector.TryAppend(
            """{"type":"speechComplete","turnId":0,"transcript":"Hello Ellai."}""",
            out var first));
        Assert.Equal(0, first?.TurnId);
        Assert.False(collector.TryAppend(
            """{"type":"transcript","turnId":0,"final":true,"transcript":"Hello Ellai."}"""));
        Assert.True(collector.TryAppend(
            """{"type":"speechComplete","turnId":1,"transcript":"Another question."}"""));
        Assert.Equal([0L, 1L], collector.Turns.Select(turn => turn.TurnId));
    }

    [Fact]
    public void PushToTalkIgnoresPartialsAndCollectsOnlyFinalTranscript()
    {
        var collector = new MetaAsrRealtimeTranscriptCollector();

        Assert.False(collector.TryAppend("""{"type":"transcript","transcript":"hello","final":false}"""));
        Assert.True(collector.TryAppend("""{"type":"transcript","transcript":"  Hello there.  ","final":true}"""));
        Assert.Equal(["Hello there."], collector.Transcripts);
    }

    [Fact]
    public void FinalTranscriptCanBeDeliveredAsItArrivesOnTheLiveProviderStream()
    {
        var collector = new MetaAsrRealtimeTranscriptCollector();

        Assert.True(collector.TryAppend(
            """{"type":"speechComplete","turnId":42,"transcript":"Submit this turn."}""",
            out var completed));

        Assert.Equal(42, completed?.TurnId);
        Assert.Equal("Submit this turn.", completed?.Text);
        Assert.False(collector.TryAppend(
            """{"type":"speechComplete","turnId":42,"transcript":"Duplicate."}""",
            out _));
    }

    [Fact]
    public void PartialTranscriptCanBeReadForDisplayWithoutEnteringFinalCollection()
    {
        var collector = new MetaAsrRealtimeTranscriptCollector();

        Assert.True(collector.TryReadPartial(
            """{"type":"transcript","transcript":"  currently speaking  ","final":false,"turnId":4}""",
            out var partial));

        Assert.Equal(4, partial?.TurnId);
        Assert.Equal("currently speaking", partial?.Text);
        Assert.Empty(collector.Turns);
        Assert.False(collector.TryReadPartial(
            """{"type":"transcript","transcript":"final words","final":true,"turnId":4}""",
            out _));
        Assert.False(collector.TryReadPartial(
            """{"type":"transcript","transcript":"invalid turn","final":false,"turnId":-1}""",
            out _));
    }

    [Fact]
    public void EndpointingUsesSpeechCompleteTurnIdAndSuppressesDuplicateTurn()
    {
        var collector = new MetaAsrRealtimeTranscriptCollector();

        Assert.False(collector.TryAppend("""{"type":"speechEnd","turnId":12}"""));
        Assert.True(collector.TryAppend("""{"type":"speechComplete","turnId":12,"transcript":"First turn."}"""));
        Assert.False(collector.TryAppend("""{"type":"speechComplete","turnId":12,"transcript":"Duplicate turn."}"""));
        Assert.True(collector.TryAppend("""{"type":"speechComplete","turnId":13,"transcript":"First turn."}"""));

        Assert.Equal(["First turn.", "First turn."], collector.Transcripts);
    }

    [Fact]
    public void OverlappingEndpointedTurnsAreReturnedInTurnOrder()
    {
        var collector = new MetaAsrRealtimeTranscriptCollector();

        Assert.True(collector.TryAppend("""{"type":"speechComplete","turnId":2,"transcript":"Second spoken turn."}"""));
        Assert.True(collector.TryAppend("""{"type":"speechComplete","turnId":1,"transcript":"First spoken turn."}"""));

        Assert.Equal([1L, 2L], collector.Turns.Select(turn => turn.TurnId));
        Assert.Equal(["First spoken turn.", "Second spoken turn."], collector.Transcripts);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"type":"speechComplete","transcript":"missing turn id"}""")]
    [InlineData("""{"type":"transcript","transcript":"partial","final":true,"turnId":-1}""")]
    public void RejectsMalformedOrUnidentifiableCompletion(string payload)
    {
        var collector = new MetaAsrRealtimeTranscriptCollector();

        Assert.False(collector.TryAppend(payload));
        Assert.Empty(collector.Transcripts);
    }
}
