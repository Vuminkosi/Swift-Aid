using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T07")]
public sealed class EllaiLiveTopicReadTests
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true);

    private static EllaiToolBroker GrantedBroker(EllaiTopicService? topics = null)
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, "synthetic", TimeSpan.FromHours(1));
        return new EllaiToolBroker(new EllaiPolicyService(), consent, null, null, null, topics);
    }

    private static IReadOnlyDictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static async Task<EllaiTopicService> SeededTopicsAsync()
    {
        var service = new EllaiTopicService();
        await service.TrackAsync("session-1", "Overdue queue review", new[] { "queue", "overdue" }, "case-1", "v3");
        await service.TrackAsync("session-1", "Overdue reminder planning", new[] { "reminder", "overdue" }, null, null);
        return service;
    }

    [Fact]
    public async Task AmbiguousReferenceListsCandidatesExplicitly()
    {
        var broker = GrantedBroker(await SeededTopicsAsync());

        var result = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Topic, Args(("ref", "overdue"))),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(2, result.ItemCount);
        Assert.Contains("Ambiguous", result.ResultSummary, StringComparison.Ordinal);
        Assert.True(EllaiToolBroker.MeasureEnvelopeBytes(result) <= EllaiToolBroker.MaxResultBytes);
    }

    [Fact]
    public async Task OrderedMentionResolvesOneTopic()
    {
        var broker = GrantedBroker(await SeededTopicsAsync());

        var result = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Topic, Args(("ref", "the second issue"))),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(1, result.ItemCount);
        Assert.Contains("Overdue reminder planning", result.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Ambiguous", result.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownReferenceIsAnAuthorizedEmpty()
    {
        var broker = GrantedBroker(await SeededTopicsAsync());

        var result = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Topic, Args(("ref", "zoning"))),
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(0, result.ItemCount);
    }

    [Fact]
    public async Task MissingServiceOrRefIsExplicit()
    {
        var broker = GrantedBroker(await SeededTopicsAsync());
        var missingRef = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Topic, Args()),
            CancellationToken.None);
        Assert.False(missingRef.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, missingRef.ReasonCode);

        var noService = await GrantedBroker().InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.Topic, Args(("ref", "overdue"))),
            CancellationToken.None);
        Assert.False(noService.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, noService.ReasonCode);
    }
}
