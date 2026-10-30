using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T06")]
public sealed class EllaiCycleRunnerTests
{
    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true);

    private static EllaiToolBroker GrantedBroker()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, "synthetic", TimeSpan.FromHours(1));
        return new EllaiToolBroker(new EllaiPolicyService(), consent);
    }

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
    public async Task GenerousCeilingsStillTerminateRunawayTurns()
    {
        var broker = GrantedBroker();
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
                        new EllaiToolRequest(EllaiToolNames.Now, new Dictionary<string, string>()),
                    },
                    null));
            },
            d => EllaiDecisionValidator.Validate(d),
            CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.Contains("budget exhausted", outcome.StopReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EllaiCycleRunner.MaxToolCalls, outcome.ToolCallsUsed);
        Assert.Equal(21, outcome.IterationsUsed);
        Assert.Equal(21, calls);
    }

    [Fact]
    public async Task ValidSecondTurnDecisionCompletes()
    {
        var broker = GrantedBroker();
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
    public async Task RunPublishesCorrelatedStatusSequence()
    {
        var broker = GrantedBroker();
        var runner = new EllaiCycleRunner(broker);
        var activity = new EllaiActivityStatusCoordinator();

        var outcome = await runner.RunAsync(
            Context,
            iteration => Task.FromResult(iteration == 1
                ? new EllaiModelTurn(
                    new[] { new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()) }, null)
                : new EllaiModelTurn(Array.Empty<EllaiToolRequest>(), ValidDecision())),
            d => EllaiDecisionValidator.Validate(d),
            CancellationToken.None,
            activity: activity,
            cycleId: "cycle-9",
            controllerEpoch: 7);

        Assert.True(outcome.Completed);
        var status = activity.CurrentStatus("session-1", 7);
        Assert.NotNull(status);
        Assert.Equal("Done — the result is verified.", status.Text);
        Assert.True(status.Sequence >= 5);
        Assert.Null(activity.CurrentStatus("session-1", 6));
    }

    [Fact]
    public async Task RunMirrorsProjectedStatusesIntoJournal()
    {
        var broker = GrantedBroker();
        var runner = new EllaiCycleRunner(broker);
        var activity = new EllaiActivityStatusCoordinator();
        var journal = new EllaiSessionEventJournal();

        var outcome = await runner.RunAsync(
            Context,
            iteration => Task.FromResult(iteration == 1
                ? new EllaiModelTurn(
                    new[] { new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()) }, null)
                : new EllaiModelTurn(Array.Empty<EllaiToolRequest>(), ValidDecision())),
            d => EllaiDecisionValidator.Validate(d),
            CancellationToken.None,
            activity: activity,
            cycleId: "cycle-9",
            controllerEpoch: 7,
            journal: journal);

        Assert.True(outcome.Completed);
        var page = journal.ReadSince("session-1", 0);
        var kinds = page.Events.Select(e => e.Kind).ToList();
        Assert.All(kinds, kind => Assert.Equal("activity", kind));
        Assert.Contains(page.Events, e => e.Detail == "Checking that for you…");
        Assert.Contains(page.Events, e => e.Detail == "Done — the result is verified.");
        var last = page.Events[^1];
        Assert.Equal("done", last.Ref);
    }

    [Fact]
    public async Task ConcurrentRequestsAreRejected()
    {
        var broker = GrantedBroker();
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
