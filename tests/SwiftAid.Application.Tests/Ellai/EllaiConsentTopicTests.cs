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

        Assert.True(consent.IsGranted("s-1", EllaiConsentDimension.Microphone, "demo", DateTimeOffset.UtcNow));
        Assert.False(consent.IsGranted("s-1", EllaiConsentDimension.ReasoningText, "demo", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RevocationInvalidatesImmediately()
    {
        var consent = new EllaiConsentService();
        var grant = consent.Grant("s-1", EllaiConsentDimension.Transcription, "demo", "synthetic", TimeSpan.FromHours(1));
        Assert.True(consent.IsGranted("s-1", EllaiConsentDimension.Transcription, "demo", DateTimeOffset.UtcNow));

        consent.Revoke(grant.GrantId);
        Assert.False(consent.IsGranted("s-1", EllaiConsentDimension.Transcription, "demo", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ExpiredGrantIsDenied()
    {
        var consent = new EllaiConsentService();
        consent.Grant("s-1", EllaiConsentDimension.Export, "demo", "synthetic", TimeSpan.FromMilliseconds(1));
        Thread.Sleep(50);

        Assert.False(consent.IsGranted("s-1", EllaiConsentDimension.Export, "demo", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SessionRevocationClearsAllDimensions()
    {
        var consent = new EllaiConsentService();
        consent.Grant("s-1", EllaiConsentDimension.Microphone, "demo", "synthetic", TimeSpan.FromHours(1));
        consent.Grant("s-1", EllaiConsentDimension.HistoryRetention, "demo", "synthetic", TimeSpan.FromHours(1));

        Assert.Equal(2, consent.RevokeSession("s-1"));
        Assert.False(consent.IsGranted("s-1", EllaiConsentDimension.Microphone, "demo", DateTimeOffset.UtcNow));
    }
}

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T09")]
public sealed class EllaiTopicServiceTests
{
    [Fact]
    public void OrdinalReferenceResolvesBeforeSemanticSearch()
    {
        var topics = new EllaiTopicService();
        topics.Track("Missing receipt", new[] { "receipt" }, "case-1", "v1");
        topics.Track("Overdue tasks", new[] { "tasks" }, "case-1", "v2");

        var resolved = topics.Resolve("the second issue", out var ambiguous);

        Assert.False(ambiguous);
        Assert.Single(resolved);
        Assert.Equal("Overdue tasks", resolved[0].Title);
    }

    [Fact]
    public void AmbiguousReferenceStaysExplicit()
    {
        var topics = new EllaiTopicService();
        topics.Track("Queue review", new[] { "queue" }, null, null);
        topics.Track("Queue summary", new[] { "queue" }, null, null);

        var resolved = topics.Resolve("queue", out var ambiguous);

        Assert.True(ambiguous);
        Assert.Equal(2, resolved.Count);
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
