using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiCaseFilterPayloadTests
{
    [Theory]
    [InlineData("country", "Eswatini", true)]
    [InlineData("from", "2026-11-02", true)]
    [InlineData("from", "2026-02-30", false)]
    [InlineData("from", "tomorrow", false)]
    [InlineData("sort", "oldest", true)]
    [InlineData("sort", "random", false)]
    [InlineData("search", "private\ntext", false)]
    [InlineData("unknown", "value", false)]
    public void OnlyBoundedRegisteredFilterShapesPersist(string key, string value, bool valid)
    {
        var proposal = new UiIntentProposal(UiIntentKind.SetPermittedFilter, "case-list", null, "case-list." + key,
            new Dictionary<string, string> { ["value"] = value });
        if (valid) Assert.NotNull(EllaiUiProposalPayload.Serialize([proposal]));
        else Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with { ResourceId = "case-id" }]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal, proposal]));
    }
    [Fact]
    public void ClearHasNoArguments()
    {
        var proposal = new UiIntentProposal(UiIntentKind.SetPermittedFilter, "case-list", null, "case-list.clear", new Dictionary<string, string>());
        Assert.NotNull(EllaiUiProposalPayload.Serialize([proposal]));
        Assert.Throws<ArgumentException>(() => EllaiUiProposalPayload.Serialize([proposal with { Arguments = new Dictionary<string, string> { ["value"] = "all" } }]));
    }
}
