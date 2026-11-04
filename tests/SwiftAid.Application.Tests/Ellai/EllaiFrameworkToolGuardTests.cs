using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiFrameworkToolGuardTests
{
    private static readonly EllaiToolContext Context = new("session", "epoch", "cases", null, 10, true);
    private static readonly EllaiToolRequest Request = new(EllaiToolNames.Now, new Dictionary<string, string>());

    [Fact]
    public async Task ConcurrentCallsCannotExceedBudgetAndFailureIsSticky()
    {
        var invoked = 0;
        var guard = new EllaiFrameworkToolGuard(2, _ => Task.FromResult(true), async (_, _, ct) =>
        {
            Interlocked.Increment(ref invoked);
            await Task.Delay(10, ct);
            return Allowed();
        });
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => guard.InvokeAsync(Context, Request, CancellationToken.None)));
        Assert.Equal(2, invoked);
        Assert.Equal(2, results.Count(result => result.Allowed));
        Assert.Equal(EllaiToolReasonCodes.BudgetExceeded, guard.FailureCode);
    }

    [Fact]
    public async Task RevocationDuringCallSuppressesResultAndBlocksSubsequentCalls()
    {
        var checks = 0;
        var invoked = 0;
        var guard = new EllaiFrameworkToolGuard(4, _ => Task.FromResult(++checks == 1), (_, _, _) =>
        {
            invoked++;
            return Task.FromResult(Allowed());
        });
        var first = await guard.InvokeAsync(Context, Request, CancellationToken.None);
        var second = await guard.InvokeAsync(Context, Request, CancellationToken.None);
        Assert.False(first.Allowed);
        Assert.Empty(first.Evidence);
        Assert.Empty(first.ResultSummary);
        Assert.False(second.Allowed);
        Assert.Equal(1, invoked);
        Assert.Equal(EllaiToolReasonCodes.DeniedByPolicy, guard.FailureCode);
    }

    [Fact]
    public async Task ContextBudgetAndBrokerDenialAreEnforced()
    {
        var guard = new EllaiFrameworkToolGuard(5, _ => Task.FromResult(true), (_, _, _) => Task.FromResult(Allowed()));
        Assert.True((await guard.InvokeAsync(Context with { RemainingCalls = 1 }, Request, CancellationToken.None)).Allowed);
        Assert.False((await guard.InvokeAsync(Context with { RemainingCalls = 1 }, Request, CancellationToken.None)).Allowed);
        Assert.Equal(EllaiToolReasonCodes.BudgetExceeded, guard.FailureCode);

        var denied = new EllaiFrameworkToolGuard(5, _ => Task.FromResult(true), (_, request, _) => Task.FromResult(
            new EllaiToolResult(request.ToolName, false, "denied", [], "", 0, false, EllaiToolReasonCodes.ConsentMissing)));
        await denied.InvokeAsync(Context, Request, CancellationToken.None);
        Assert.Equal(EllaiToolReasonCodes.ConsentMissing, denied.FailureCode);
    }

    private static EllaiToolResult Allowed() => new(EllaiToolNames.Now, true, null,
        Array.Empty<EvidenceRef>(), "observation", 1, false);
}
