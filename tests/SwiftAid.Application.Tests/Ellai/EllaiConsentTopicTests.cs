using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T03")]
public sealed class EllaiConsentServiceTests
{
    [Fact]
    public void MicrophoneGrantDoesNotCoverReasoning()
    {
        var consent = new EllaiConsentService();
        consent.Grant("s-1", EllaiConsentDimension.Microphone, "demo", "synthetic", TimeSpan.FromHours(1));

        Assert.True(consent.IsGranted("s-1", EllaiConsentDimension.Microphone, "demo", "synthetic", DateTimeOffset.UtcNow));
        Assert.False(consent.IsGranted("s-1", EllaiConsentDimension.ReasoningText, "demo", "synthetic", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RevocationInvalidatesImmediately()
    {
        var consent = new EllaiConsentService();
        var grant = consent.Grant("s-1", EllaiConsentDimension.Transcription, "demo", "synthetic", TimeSpan.FromHours(1));
        Assert.True(consent.IsGranted("s-1", EllaiConsentDimension.Transcription, "demo", "synthetic", DateTimeOffset.UtcNow));

        consent.Revoke(grant.GrantId);
        Assert.False(consent.IsGranted("s-1", EllaiConsentDimension.Transcription, "demo", "synthetic", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ExpiredGrantIsDenied()
    {
        var consent = new EllaiConsentService();
        consent.Grant("s-1", EllaiConsentDimension.Export, "demo", "synthetic", TimeSpan.FromMilliseconds(1));
        Thread.Sleep(50);

        Assert.False(consent.IsGranted("s-1", EllaiConsentDimension.Export, "demo", "synthetic", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SessionRevocationClearsAllDimensions()
    {
        var consent = new EllaiConsentService();
        consent.Grant("s-1", EllaiConsentDimension.Microphone, "demo", "synthetic", TimeSpan.FromHours(1));
        consent.Grant("s-1", EllaiConsentDimension.HistoryRetention, "demo", "synthetic", TimeSpan.FromHours(1));

        Assert.Equal(2, consent.RevokeSession("s-1"));
        Assert.False(consent.IsGranted("s-1", EllaiConsentDimension.Microphone, "demo", "synthetic", DateTimeOffset.UtcNow));
    }
}

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T09")]
public sealed class EllaiTopicServiceTests
{
    [Fact]
    public async Task OrdinalReferenceResolvesBeforeSemanticSearch()
    {
        var topics = new EllaiTopicService();
        await topics.TrackAsync("session-1", "Missing receipt", new[] { "receipt" }, "case-1", "v1");
        await topics.TrackAsync("session-1", "Overdue tasks", new[] { "tasks" }, "case-1", "v2");

        var (resolved, ambiguous) = await topics.ResolveAsync("session-1", "the second issue");

        Assert.False(ambiguous);
        Assert.Single(resolved);
        Assert.Equal("Overdue tasks", resolved[0].Title);
    }

    [Fact]
    public async Task AmbiguousReferenceStaysExplicit()
    {
        var topics = new EllaiTopicService();
        await topics.TrackAsync("session-1", "Queue review", new[] { "queue" }, null, null);
        await topics.TrackAsync("session-1", "Queue summary", new[] { "queue" }, null, null);

        var (resolved, ambiguous) = await topics.ResolveAsync("session-1", "queue");

        Assert.True(ambiguous);
        Assert.Equal(2, resolved.Count);
    }

    [Fact]
    public async Task TopicsNeverLeakAcrossSessions()
    {
        var topics = new EllaiTopicService();
        await topics.TrackAsync("session-1", "Queue review", new[] { "queue" }, null, null);

        var (resolved, _) = await topics.ResolveAsync("session-2", "queue");

        Assert.Empty(resolved);
    }

    [Fact]
    public async Task RetrackingUpdatesOneStableRow()
    {
        var topics = new EllaiTopicService();
        var first = await topics.TrackAsync("session-1", "Queue review", new[] { "queue" }, null, "v1");
        var second = await topics.TrackAsync("session-1", "Queue review", new[] { "queue" }, null, "v2");

        Assert.Equal(first.TopicId, second.TopicId);
        Assert.Equal("v2", second.EntityVersion);
    }

    [Fact]
    public void SixthDetourRequiresExplicitChoice()
    {
        var topics = new EllaiTopicService();
        var position = new GuidancePosition("a", "j", "1", "h", "s", "v", 1, 1, 1, 1);
        for (var i = 0; i < EllaiTopicService.MaxReturnFrames; i++)
        {
            Assert.True(topics.TryPushReturn(new GuidanceReturnFrame(
                position, GuidanceMode.Presentation, null, null, null, "b",
                Array.Empty<string>(), Array.Empty<string>())));
        }

        Assert.False(topics.TryPushReturn(new GuidanceReturnFrame(
            position, GuidanceMode.Presentation, null, null, null, "b",
            Array.Empty<string>(), Array.Empty<string>())));
        Assert.NotNull(topics.PopReturn());
    }
}
