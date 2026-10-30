using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T07")]
public sealed class EllaiReadToolTests
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true);

    [Fact]
    public async Task NestedCallsCannotBypassBoundOrScope()
    {
        var broker = new EllaiToolBroker(new EllaiPolicyService());

        var unregistered = await broker.InvokeAsync(
            Context, new EllaiToolRequest("ellai_sql", new Dictionary<string, string>()),
            CancellationToken.None);
        Assert.False(unregistered.Allowed);

        var exhausted = await broker.InvokeAsync(
            Context with { RemainingCalls = 0 },
            new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()),
            CancellationToken.None);
        Assert.False(exhausted.Allowed);
        Assert.Contains("budget", exhausted.DenialReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var codeArg = await broker.InvokeAsync(
            Context,
            new EllaiToolRequest(EllaiToolNames.Evidence,
                new Dictionary<string, string> { ["q"] = "SELECT * FROM cases" }),
            CancellationToken.None);
        Assert.False(codeArg.Allowed);
    }

    [Fact]
    public async Task RegisteredReadsReturnBoundedResults()
    {
        var broker = new EllaiToolBroker(new EllaiPolicyService());

        foreach (var tool in EllaiToolNames.All)
        {
            var result = await broker.InvokeAsync(
                Context, new EllaiToolRequest(tool, new Dictionary<string, string>()),
                CancellationToken.None);
            Assert.True(result.Allowed);
            Assert.True(result.ItemCount <= EllaiToolBroker.MaxItems);
        }
    }

    [Fact]
    public async Task MissingScopeIsDenied()
    {
        var broker = new EllaiToolBroker(new EllaiPolicyService());

        var result = await broker.InvokeAsync(
            Context with { ScopeEpoch = string.Empty },
            new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.False(result.Allowed);
    }
}
