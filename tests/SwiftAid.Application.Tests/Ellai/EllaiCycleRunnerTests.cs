using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T06")]
public sealed class EllaiCycleRunnerTests
{
    [Fact]
    public async Task IsolatedParallelToolBatchStartsTogetherAndAcknowledgesOnlyOnce()
    {
        var broker = GrantedBroker();
        var runner = new EllaiCycleRunner(broker);
        var responses = new EllaiSessionResponseStore();
        var progress = new EllaiToolProgressPublisher(responses, Context.SessionId, "parallel", 1, 1);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var run = runner.RunAsync(Context, iteration => Task.FromResult(iteration == 1
            ? new EllaiModelTurn(Enumerable.Range(1, 4).Select(index => new EllaiToolRequest(
                EllaiToolNames.Now, new Dictionary<string, string>(), index.ToString())).ToArray(), null)
            : new EllaiModelTurn([], ValidDecision())), EllaiDecisionValidator.Validate, CancellationToken.None,
            onActivity: progress.Observe, invokeToolAsync: async (context, request, ct) =>
            {
                if (Interlocked.Increment(ref started) == 4) allStarted.TrySetResult();
                await release.Task.WaitAsync(ct);
                return await broker.InvokeAsync(context, request, ct);
            });
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(run.IsCompleted);
            Assert.Single(responses.ReadSince(Context.SessionId, 0).Responses,
                row => row.Progress?.State == "Acknowledgement");
        }
        finally { release.TrySetResult(); }
        var outcome = await run;
        Assert.True(outcome.Completed);
        Assert.Equal(4, outcome.ToolCallsUsed);
    }

    [Fact]
    public async Task InvalidToolArgumentsReturnToModelForBoundedRepair()
    {
        var observations = new List<EllaiToolResult>();
        var runner = new EllaiCycleRunner(GrantedBroker());
        var outcome = await runner.RunAsync(Context, iteration => Task.FromResult(iteration switch
        {
            1 => new EllaiModelTurn([
                new(EllaiToolNames.ProductFacts, new Dictionary<string, string> { ["query"] = "volunteer" }),
                new(EllaiToolNames.Now, new Dictionary<string, string>()),
            ], ValidDecision()),
            2 => new EllaiModelTurn([new(EllaiToolNames.ProductFacts, new Dictionary<string, string>())], null),
            _ => new EllaiModelTurn([], ValidDecision()),
        }), EllaiDecisionValidator.Validate, CancellationToken.None,
            onToolResult: (_, result) => observations.Add(result));
        Assert.True(outcome.Completed);
        Assert.Equal(3, outcome.IterationsUsed);
        Assert.Equal(3, outcome.ToolCallsUsed);
        Assert.Equal(EllaiToolReasonCodes.InvalidArguments, observations[0].ReasonCode);
        Assert.True(observations[1].Allowed);
        Assert.True(observations[2].Allowed);
    }

    private static readonly EllaiToolContext Context = new(
        "session-1", "epoch-1", "cases", "case-1", 4, true, "consent/1", EllaiProcessingClassification.SyntheticDemo);

    private static EllaiToolBroker GrantedBroker()
    {
        var consent = new EllaiConsentService();
        consent.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
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
    public async Task InvestigationCanCompleteAfterMoreThanTwentyFourToolCalls()
    {
        var runner = new EllaiCycleRunner(GrantedBroker());
        var outcome = await runner.RunAsync(Context,
            iteration => Task.FromResult(iteration == 1
                ? new EllaiModelTurn(Enumerable.Range(0, 30).Select(_ =>
                    new EllaiToolRequest(EllaiToolNames.Now, new Dictionary<string, string>())).ToArray(), null)
                : new EllaiModelTurn(Array.Empty<EllaiToolRequest>(), ValidDecision())),
            d => EllaiDecisionValidator.Validate(d), CancellationToken.None);
        Assert.True(outcome.Completed, outcome.StopReason);
        Assert.Equal(30, outcome.ToolCallsUsed);
    }

    [Fact]
    public async Task GenerousCeilingsStillTerminateRunawayTurns()
    {
        Assert.Equal(4_000, EllaiCycleRunner.MaxToolCalls);
        var broker = GrantedBroker();
        var runner = new EllaiCycleRunner(broker);
        var calls = 0;

        var outcome = await runner.RunAsync(
            Context,
            iteration =>
            {
                calls++;
                return Task.FromResult(new EllaiModelTurn(
                    Enumerable.Range(0, 1_000).Select(_ =>
                        new EllaiToolRequest(EllaiToolNames.Now, new Dictionary<string, string>())).ToArray(),
                    null));
            },
            d => EllaiDecisionValidator.Validate(d),
            CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.Contains("budget exhausted", outcome.StopReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EllaiCycleRunner.MaxToolCalls, outcome.ToolCallsUsed);
        Assert.Equal(5, outcome.IterationsUsed);
        Assert.Equal(5, calls);
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
    public async Task ExhaustedReadsStopAsUnreviewed()
    {
        var broker = GrantedBroker();
        var runner = new EllaiCycleRunner(broker);
        var activity = new EllaiActivityStatusCoordinator();

        var outcome = await runner.RunAsync(
            Context,
            _ => Task.FromResult(new EllaiModelTurn(
                new[] { new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>()) }, null)),
            d => EllaiDecisionValidator.Validate(d),
            CancellationToken.None,
            activity: activity,
            cycleId: "cycle-9",
            controllerEpoch: 1);

        Assert.False(outcome.Completed);
        Assert.Equal(EllaiCycleRunner.MaxIterations, outcome.IterationsUsed);
        Assert.Equal(EllaiCycleRunner.MaxIterations, outcome.ToolCallsUsed);
        var status = activity.CurrentStatus("session-1", 1);
        Assert.NotNull(status);
        Assert.Equal(
            "I found information, but need another authorized step to finish reviewing it.",
            status.Text);
    }

    [Fact]
    public async Task ExhaustedReasoningIterationsDoNotClaimTheTokenBudgetWasUsed()
    {
        var runner = new EllaiCycleRunner(GrantedBroker());
        var activity = new EllaiActivityStatusCoordinator();

        var outcome = await runner.RunAsync(
            Context,
            _ => Task.FromResult(new EllaiModelTurn(Array.Empty<EllaiToolRequest>(), null)),
            EllaiDecisionValidator.Validate,
            CancellationToken.None,
            activity: activity,
            controllerEpoch: 1);

        Assert.False(outcome.Completed);
        Assert.Contains("iteration limit", outcome.StopReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Ellai reached this request's reasoning limit before it could finish. Try a narrower question.",
            activity.CurrentStatus("session-1", 1)?.Text);
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
