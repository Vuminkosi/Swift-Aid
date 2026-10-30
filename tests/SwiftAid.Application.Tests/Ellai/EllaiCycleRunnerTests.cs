using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T06")]
public sealed class EllaiCycleRunnerTests
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true);

    private static EllaiDecision ValidDecision() => new(
        2, DecisionKind.Answer, "Queue reviewed.", "Queue reviewed.",
        new[]
        {
            new GroundedClaim("c1", "Queue has 3 items.", ClaimKind.Observed,
                new[] { new EvidenceRef("queue", "q1", "v3", null, "e1") })
        },
        Array.Empty<EllaiQuestion>(), Array.Empty<UiIntentProposal>(),
        null, Array.Empty<string>(), null, null);

    [Fact]
    public async Task ThirdIterationAndFourthToolCallCannotStartHiddenContinuation()
    {
        var broker = new EllaiToolBroker(new EllaiPolicyService());
        var runner = new EllaiCycleRunner(broker);
        var calls = 0;

        var outcome = await runner.RunAsync(
            Context,
            iteration =>
            {
                calls++;
                return Task.FromResult(new EllaiModelTurn(
                    new[]
                    {
                        new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()),
                        new EllaiToolRequest(EllaiToolNames.Capabilities, new Dictionary<string, string>()),
                    },
                    iteration == 3 ? ValidDecision() : null));
            },
            d => EllaiDecisionValidator.Validate(d),
            CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.False(outcome.Completed);
        Assert.Contains("budget exhausted", outcome.StopReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, outcome.ToolCallsUsed);
    }

    [Fact]
    public async Task ValidSecondTurnDecisionCompletes()
    {
        var broker = new EllaiToolBroker(new EllaiPolicyService());
        var runner = new EllaiCycleRunner(broker);

        var outcome = await runner.RunAsync(
            Context,
            iteration => Task.FromResult(iteration == 1
                ? new EllaiModelTurn(
                    new[] { new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()) }, null)
                : new EllaiModelTurn(Array.Empty<EllaiToolRequest>(), ValidDecision())),
            d => EllaiDecisionValidator.Validate(d),
            CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.NotNull(outcome.Decision);
        Assert.Equal(2, outcome.IterationsUsed);
        Assert.Equal(1, outcome.ToolCallsUsed);
    }

    [Fact]
    public async Task ConcurrentRequestsAreRejected()
    {
        var broker = new EllaiToolBroker(new EllaiPolicyService());
        var runner = new EllaiCycleRunner(broker);
        var gate = new TaskCompletionSource();

        var first = runner.RunAsync(
            Context,
            async _ => { await gate.Task; return new EllaiModelTurn(Array.Empty<EllaiToolRequest>(), ValidDecision()); },
            d => EllaiDecisionValidator.Validate(d),
            CancellationToken.None);

        var second = await runner.RunAsync(
            Context,
            _ => Task.FromResult(new EllaiModelTurn(Array.Empty<EllaiToolRequest>(), ValidDecision())),
            d => EllaiDecisionValidator.Validate(d),
            CancellationToken.None);

        Assert.False(second.Completed);
        Assert.Contains("active", second.StopReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        gate.SetResult();
        var completed = await first;
        Assert.True(completed.Completed);
    }
}
