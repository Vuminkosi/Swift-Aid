using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
public sealed class EllaiSessionResponseStoreTests
{
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
}
