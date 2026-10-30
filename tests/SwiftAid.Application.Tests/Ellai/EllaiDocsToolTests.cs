using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T00")]
public sealed class EllaiDocsToolTests : IDisposable
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true);

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"technical-{Guid.NewGuid():N}.md");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    private void WriteSource(string body) => File.WriteAllText(_path, body);

    private static string SampleSource() => """
        # Reference

        ## 1. Tool broker catalogue

        **Status: Implemented.** The broker exposes ellai_state and friends.

        ## 2. Approval dialog

        **Status: Stub.** The dialog opens but edit starts no new version yet.

        ## 3. Budgets

        Spending caps and token budgets govern every cycle.
        """;

    private static EllaiToolBroker GrantedBroker(EllaiTechnicalDocs? docs = null)
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, "synthetic", TimeSpan.FromHours(1));
        return new EllaiToolBroker(new EllaiPolicyService(), consent, docs);
    }

    private static IReadOnlyDictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void SearchPrefersExactSectionIdThenTitleThenBody()
    {
        WriteSource(SampleSource());
        var docs = new EllaiTechnicalDocs(_path);

        var exact = docs.Search("2-approval-dialog");
        Assert.NotEmpty(exact);
        Assert.Equal("2-approval-dialog", exact[0].SectionId);

        var titled = docs.Search("budgets");
        Assert.Contains(titled, h => h.SectionId == "3-budgets");

        Assert.Equal(docs.Revision(), exact[0].Revision);
        Assert.Equal("Stub.", exact[0].Status);
    }

    [Fact]
    public void GetReturnsBoundedExcerptWithProvenance()
    {
        WriteSource(SampleSource());
        var docs = new EllaiTechnicalDocs(_path);

        var section = docs.Get("1-tool-broker-catalogue");
        Assert.NotNull(section);
        Assert.Equal("technical.md", section.DocumentId);
        Assert.Contains("ellai_state", section.Body, StringComparison.Ordinal);
        Assert.True(section.Body.Length <= EllaiTechnicalDocs.MaxExcerptChars + 64);

        Assert.Null(docs.Get("no-such-section"));
    }

    [Fact]
    public void MissingSourceReportsUnavailableWithoutThrowing()
    {
        var docs = new EllaiTechnicalDocs(_path);
        Assert.False(docs.IsAvailable);
        Assert.Empty(docs.Search("anything"));
        Assert.Null(docs.Get("1-tool-broker-catalogue"));
    }

    [Fact]
    public async Task DocsToolsAreUnavailableWithoutASource()
    {
        var broker = GrantedBroker();

        var search = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.DocsSearch, Args(("q", "budgets"))),
            CancellationToken.None);
        Assert.False(search.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, search.ReasonCode);

        var get = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.DocsGet, Args(("section", "3-budgets"))),
            CancellationToken.None);
        Assert.False(get.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Unavailable, get.ReasonCode);
    }

    [Fact]
    public async Task DocsToolsReturnSourceLinkedResultsWithinBounds()
    {
        WriteSource(SampleSource());
        var broker = GrantedBroker(new EllaiTechnicalDocs(_path));

        var search = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.DocsSearch, Args(("q", "broker"))),
            CancellationToken.None);
        Assert.True(search.Allowed);
        Assert.Equal(EllaiToolReasonCodes.Ok, search.ReasonCode);
        Assert.Contains("1-tool-broker-catalogue", search.ResultSummary, StringComparison.Ordinal);
        Assert.True(EllaiToolBroker.MeasureEnvelopeBytes(search) <= EllaiToolBroker.MaxResultBytes);

        var get = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.DocsGet, Args(("section", "3-budgets"))),
            CancellationToken.None);
        Assert.True(get.Allowed);
        Assert.Contains("Spending caps", get.ResultSummary, StringComparison.Ordinal);

        var unknown = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.DocsGet, Args(("section", "9-missing"))),
            CancellationToken.None);
        Assert.False(unknown.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, unknown.ReasonCode);

        var badLimit = await broker.InvokeAsync(
            Context, new EllaiToolRequest(EllaiToolNames.DocsSearch, Args(("q", "x"), ("limit", "99"))),
            CancellationToken.None);
        Assert.False(badLimit.Allowed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, badLimit.ReasonCode);
    }
}
